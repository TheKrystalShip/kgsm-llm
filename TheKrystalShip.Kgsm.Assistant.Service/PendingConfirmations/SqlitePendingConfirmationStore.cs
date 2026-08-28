using System.Security.Cryptography;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Ports;

using TheKrystalShip.Llm.Conversation;

namespace TheKrystalShip.Kgsm.Assistant.Service.PendingConfirmations;

/// <summary>
/// SQLite-backed <see cref="IPendingConfirmationStore"/>, sharing the SAME database FILE as the
/// conversation history (<see cref="ConversationOptions.DatabasePath"/>) rather than opening a second
/// one — one state file for the Service's durable data, one less path to configure and back up. Adds
/// its own <c>pending_confirmations</c> table with an idempotent create, so it assumes nothing about
/// load order relative to the conversation store.
/// </summary>
/// <remarks>
/// Durable rather than in-memory, which is what lets a staged action outlive a Service restart within
/// its lifetime — an operator restarting the assistant does not silently void a confirmation someone
/// is looking at. SQLite is single-writer, so writes are serialised through a process lock exactly as
/// the conversation store does; WAL still lets reads run concurrently.
/// </remarks>
internal sealed class SqlitePendingConfirmationStore : IPendingConfirmationStore
{
    private readonly string _connectionString;
    private readonly object _writeGate = new();

    private readonly IAssistantJournal _journal;

    public SqlitePendingConfirmationStore(
        IOptions<ConversationOptions> options, IAssistantJournal journal)
    {
        _connectionString = StateDatabase.ConnectionString(options.Value);
        _journal = journal;
        Initialize();
    }

    private void Initialize()
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        // config_value holds whatever the kind carries there, including a write_file body or a
        // blueprint draft — there is no size ceiling to work around now that it never leaves the host.
        cmd.CommandText =
            """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS pending_confirmations (
                id            TEXT PRIMARY KEY,
                kind          INTEGER NOT NULL,
                target        TEXT NOT NULL,
                instance_name TEXT,
                config_key    TEXT,
                config_value  TEXT,
                staged_by     TEXT NOT NULL,
                expires_at    TEXT NOT NULL,
                announce_provider TEXT,
                announce_name     TEXT,
                announced_at      TEXT,
                conversation_id   TEXT,
                library           TEXT
            );
            """;
        cmd.ExecuteNonQuery();

        // A database created before this table carried the announcement columns keeps its rows; SQLite
        // has no "add column if absent", and a duplicate-column error is the ordinary answer on every
        // start after the first rather than a fault.
        foreach (var column in (string[])
                 ["announce_provider TEXT", "announce_name TEXT", "announced_at TEXT",
                  "conversation_id TEXT", "library TEXT"])
        {
            try
            {
                using var alter = connection.CreateCommand();
                alter.CommandText = $"ALTER TABLE pending_confirmations ADD COLUMN {column};";
                alter.ExecuteNonQuery();
            }
            catch (SqliteException)
            {
                // Already there.
            }
        }
    }

    public string Put(
        PendingConfirmation confirmation,
        string userId,
        DateTimeOffset expiry,
        ConfirmationStager? announceTo = null,
        string? conversationId = null)
    {
        ArgumentNullException.ThrowIfNull(confirmation);

        var id = NewHandle();
        lock (_writeGate)
        {
            using var connection = Open();

            SweepExpired(connection);

            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO pending_confirmations
                    (id, kind, target, instance_name, config_key, config_value, staged_by, expires_at,
                     announce_provider, announce_name, conversation_id, library)
                VALUES ($id, $kind, $target, $instance, $ckey, $cvalue, $by, $expires,
                        $provider, $name, $conversation, $library);
                """;
            // Both null is the ordinary case and is what makes a row unannounceable: there is nobody
            // recorded to announce it to, which is the same statement as "do not".
            cmd.Parameters.AddWithValue("$provider", (object?)announceTo?.Provider ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$name", (object?)announceTo?.DisplayName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$conversation", (object?)conversationId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$kind", (int)confirmation.Kind);
            cmd.Parameters.AddWithValue("$target", confirmation.Target);
            cmd.Parameters.AddWithValue("$instance", (object?)confirmation.InstanceName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ckey", (object?)confirmation.ConfigKey ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$cvalue", (object?)confirmation.ConfigValue ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$library", (object?)confirmation.Library ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$by", userId ?? string.Empty);
            cmd.Parameters.AddWithValue("$expires", expiry.ToString("O"));
            cmd.ExecuteNonQuery();
        }

        // Recorded HERE rather than at each caller, because every staging path — the streamed turn, the
        // buffered one, and a blueprint draft re-staged at confirm time — comes through this one method.
        // A proposal is otherwise invisible: nothing has run, so the engine has no record of it, and one
        // that expires unapproved leaves no trace anywhere on the host.
        //
        // The handle is deliberately not passed. It IS the capability that redeems the action, and the
        // journal is readable by anything that can open the directory.
        _journal.ActionProposed(
            confirmation.Kind.ToString(),
            tool: null,
            confirmation.InstanceName ?? confirmation.Target,
            (long)Math.Max(0, (expiry - DateTimeOffset.UtcNow).TotalSeconds));

        return id;
    }

    public bool TryTake(string? id, string userId, out PendingConfirmation confirmation)
    {
        confirmation = null!;
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrEmpty(userId))
            return false;

        lock (_writeGate)
        {
            using var connection = Open();

            PendingConfirmation? found = null;
            var by = string.Empty;
            DateTimeOffset expiresAt = default;

            using (var select = connection.CreateCommand())
            {
                select.CommandText =
                    """
                    SELECT kind, target, instance_name, config_key, config_value, staged_by, expires_at,
                           library
                    FROM pending_confirmations WHERE id = $id;
                    """;
                select.Parameters.AddWithValue("$id", id);
                using var reader = select.ExecuteReader();
                if (reader.Read())
                {
                    found = new PendingConfirmation(
                        (ConfirmationKind)reader.GetInt32(0),
                        reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        reader.IsDBNull(7) ? null : reader.GetString(7));
                    by = reader.GetString(5);
                    expiresAt = DateTimeOffset.Parse(reader.GetString(6));
                }
            }

            if (found is null)
                return false;

            // Somebody else's handle is left exactly as it was — refusing costs the caller nothing,
            // where consuming it would let a wrong guess cancel an action its owner is looking at.
            if (!string.Equals(by, userId, StringComparison.Ordinal))
                return false;

            // Otherwise single-use regardless of outcome: delete now, so a row read past its expiry
            // never lingers for a second take and a redeemed one is never redeemed twice.
            using (var delete = connection.CreateCommand())
            {
                delete.CommandText = "DELETE FROM pending_confirmations WHERE id = $id;";
                delete.Parameters.AddWithValue("$id", id);
                delete.ExecuteNonQuery();
            }

            if (expiresAt < DateTimeOffset.UtcNow)
                return false;

            // An unrecognised kind is a row this build cannot act on — refuse rather than cast a
            // number into an enum the executor will not understand.
            if (!Enum.IsDefined(typeof(ConfirmationKind), found.Kind))
                return false;

            confirmation = found;
            return true;
        }
    }

    public IReadOnlyList<WaitingConfirmation> Unannounced()
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, kind, target, instance_name, config_key, config_value, staged_by, expires_at,
                   announce_provider, announce_name, library
            FROM pending_confirmations
            WHERE announce_provider IS NOT NULL AND announced_at IS NULL AND expires_at > $now
            ORDER BY expires_at;
            """;
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));

        var waiting = new List<WaitingConfirmation>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var kind = (ConfirmationKind)reader.GetInt32(1);
            // A row this build cannot describe is not one to announce: the notification would have to
            // name an action whose kind means nothing here.
            if (!Enum.IsDefined(kind))
                continue;

            waiting.Add(new WaitingConfirmation(
                reader.GetString(0),
                new ConfirmationStager(
                    reader.GetString(8), reader.GetString(6), reader.IsDBNull(9) ? "" : reader.GetString(9)),
                new PendingConfirmation(
                    kind,
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(10) ? null : reader.GetString(10)),
                DateTimeOffset.Parse(reader.GetString(7))));
        }
        return waiting;
    }

    public IReadOnlyList<StagedProposal> PendingFor(string userId, string conversationId)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(conversationId))
            return [];

        using var connection = Open();
        using var cmd = connection.CreateCommand();
        // Scoped to the caller as well as the conversation: the id is derived from their own principal
        // upstream, and matching on both means a scoping mistake there cannot hand somebody another
        // person's staged action.
        cmd.CommandText =
            """
            SELECT id, kind, target, instance_name, config_key, config_value, expires_at, library
            FROM pending_confirmations
            WHERE staged_by = $by AND conversation_id = $conversation AND expires_at > $now
            ORDER BY expires_at;
            """;
        cmd.Parameters.AddWithValue("$by", userId);
        cmd.Parameters.AddWithValue("$conversation", conversationId);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));

        var pending = new List<StagedProposal>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var kind = (ConfirmationKind)reader.GetInt32(1);
            if (!Enum.IsDefined(kind))
                continue;

            pending.Add(new StagedProposal(
                reader.GetString(0),
                new PendingConfirmation(
                    kind,
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(7) ? null : reader.GetString(7)),
                DateTimeOffset.Parse(reader.GetString(6))));
        }
        return pending;
    }

    public void MarkAnnounced(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;

        lock (_writeGate)
        {
            using var connection = Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "UPDATE pending_confirmations SET announced_at = $now WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 16 bytes from the cryptographic RNG, hex-encoded. The handle is the capability, so it is
    /// unguessable by construction and carries nothing about what it redeems; 32 characters leaves
    /// room inside every surface's identifier limits, Discord's 100-character button id included.
    /// </summary>
    private static string NewHandle()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>Opportunistic cleanup of rows past their lifetime, run inline with every
    /// <see cref="Put"/> — an action is staged far less often than the table is read, so this needs
    /// no timer of its own.</summary>
    private static void SweepExpired(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM pending_confirmations WHERE expires_at < $now;";
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
