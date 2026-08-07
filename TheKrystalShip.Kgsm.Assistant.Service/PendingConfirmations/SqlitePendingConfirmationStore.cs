using System.Security.Cryptography;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

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

    public SqlitePendingConfirmationStore(IOptions<ConversationOptions> options)
    {
        var value = options.Value;

        // Same default-path rule as SqliteConversationStore: a configured path wins, otherwise a
        // file beside the host binary — this store always shares whichever file that store picked.
        var databasePath = string.IsNullOrWhiteSpace(value.DatabasePath)
            ? Path.Combine(AppContext.BaseDirectory, "conversations.db")
            : value.DatabasePath;

        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 5,
        }.ToString();

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
                expires_at    TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public string Put(PendingConfirmation confirmation, string userId, DateTimeOffset expiry)
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
                    (id, kind, target, instance_name, config_key, config_value, staged_by, expires_at)
                VALUES ($id, $kind, $target, $instance, $ckey, $cvalue, $by, $expires);
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$kind", (int)confirmation.Kind);
            cmd.Parameters.AddWithValue("$target", confirmation.Target);
            cmd.Parameters.AddWithValue("$instance", (object?)confirmation.InstanceName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ckey", (object?)confirmation.ConfigKey ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$cvalue", (object?)confirmation.ConfigValue ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$by", userId ?? string.Empty);
            cmd.Parameters.AddWithValue("$expires", expiry.ToString("O"));
            cmd.ExecuteNonQuery();
        }
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
                    SELECT kind, target, instance_name, config_key, config_value, staged_by, expires_at
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
                        reader.IsDBNull(4) ? null : reader.GetString(4));
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
