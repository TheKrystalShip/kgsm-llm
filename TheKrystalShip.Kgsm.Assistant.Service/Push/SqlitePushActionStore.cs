using System.Security.Cryptography;

using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Service.PendingConfirmations;
using TheKrystalShip.Llm.Conversation;

namespace TheKrystalShip.Kgsm.Assistant.Service.Push;

/// <inheritdoc/>
/// <remarks>
/// Durable rather than in-memory for the same reason the confirmations it points at are: restarting
/// the assistant must not silently void a notification somebody is looking at on a lock screen.
/// </remarks>
internal sealed class SqlitePushActionStore : IPushActionStore
{
    private readonly string _connectionString;
    private readonly object _writeGate = new();

    public SqlitePushActionStore(IOptions<ConversationOptions> options)
    {
        _connectionString = StateDatabase.ConnectionString(options.Value);
        Initialize();
    }

    private void Initialize()
    {
        using var connection = StateDatabase.Open(_connectionString);
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS push_actions (
                handle              TEXT PRIMARY KEY,
                verb                INTEGER NOT NULL,
                confirmation_handle TEXT NOT NULL,
                provider            TEXT NOT NULL,
                user_id             TEXT NOT NULL,
                display_name        TEXT NOT NULL,
                endpoint            TEXT NOT NULL,
                expires_at          TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_push_actions_endpoint
                ON push_actions (endpoint);
            CREATE INDEX IF NOT EXISTS ix_push_actions_confirmation
                ON push_actions (confirmation_handle);
            """;
        cmd.ExecuteNonQuery();
    }

    public string Mint(
        PushActionVerb verb,
        string confirmationHandle,
        ConfirmationStager stager,
        string endpoint,
        DateTimeOffset expiry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmationHandle);
        ArgumentNullException.ThrowIfNull(stager);
        ArgumentException.ThrowIfNullOrWhiteSpace(stager.UserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        var handle = NewHandle();
        lock (_writeGate)
        {
            using var connection = StateDatabase.Open(_connectionString);

            SweepExpired(connection);

            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO push_actions
                    (handle, verb, confirmation_handle, provider, user_id, display_name, endpoint,
                     expires_at)
                VALUES ($handle, $verb, $confirmation, $provider, $user, $name, $endpoint, $expires);
                """;
            cmd.Parameters.AddWithValue("$handle", handle);
            cmd.Parameters.AddWithValue("$verb", (int)verb);
            cmd.Parameters.AddWithValue("$confirmation", confirmationHandle);
            cmd.Parameters.AddWithValue("$provider", stager.Provider);
            cmd.Parameters.AddWithValue("$user", stager.UserId);
            cmd.Parameters.AddWithValue("$name", stager.DisplayName);
            cmd.Parameters.AddWithValue("$endpoint", endpoint);
            cmd.Parameters.AddWithValue("$expires", expiry.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        return handle;
    }

    public bool TryTake(string? handle, out PushAction action)
    {
        action = null!;
        if (string.IsNullOrWhiteSpace(handle))
            return false;

        lock (_writeGate)
        {
            using var connection = StateDatabase.Open(_connectionString);

            PushAction? found = null;
            DateTimeOffset expiresAt = default;

            using (var select = connection.CreateCommand())
            {
                select.CommandText =
                    """
                    SELECT verb, confirmation_handle, provider, user_id, display_name, endpoint,
                           expires_at
                    FROM push_actions WHERE handle = $handle;
                    """;
                select.Parameters.AddWithValue("$handle", handle);
                using var reader = select.ExecuteReader();
                if (reader.Read())
                {
                    found = new PushAction(
                        (PushActionVerb)reader.GetInt32(0),
                        reader.GetString(1),
                        new ConfirmationStager(
                            reader.GetString(2), reader.GetString(3), reader.GetString(4)),
                        reader.GetString(5));
                    expiresAt = DateTimeOffset.Parse(reader.GetString(6));
                }
            }

            if (found is null)
                return false;

            // Consumed whatever happens next, including when it turns out to be expired: a row read
            // once must never be readable again, or a retry re-runs what the first read authorised.
            // Both buttons for the same confirmation go with it — tapping Confirm settles the question
            // Cancel was asking, and leaving its handle standing is leaving a live capability behind.
            using (var delete = connection.CreateCommand())
            {
                delete.CommandText =
                    "DELETE FROM push_actions WHERE confirmation_handle = $confirmation;";
                delete.Parameters.AddWithValue("$confirmation", found.ConfirmationHandle);
                delete.ExecuteNonQuery();
            }

            if (expiresAt < DateTimeOffset.UtcNow)
                return false;

            if (!Enum.IsDefined(found.Verb))
                return false;

            action = found;
            return true;
        }
    }

    public void VoidFor(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return;

        lock (_writeGate)
        {
            using var connection = StateDatabase.Open(_connectionString);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM push_actions WHERE endpoint = $endpoint;";
            cmd.Parameters.AddWithValue("$endpoint", endpoint);
            cmd.ExecuteNonQuery();
        }
    }

    public void VoidForConfirmation(string confirmationHandle)
    {
        if (string.IsNullOrWhiteSpace(confirmationHandle))
            return;

        lock (_writeGate)
        {
            using var connection = StateDatabase.Open(_connectionString);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM push_actions WHERE confirmation_handle = $confirmation;";
            cmd.Parameters.AddWithValue("$confirmation", confirmationHandle);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 16 bytes from the cryptographic RNG, hex-encoded — the same 32 characters a confirmation handle
    /// is, and unguessable for the same reason: the handle <em>is</em> the capability.
    /// </summary>
    private static string NewHandle()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>Opportunistic cleanup, inline with minting: handles are written far less often than
    /// the table is read, so this needs no timer of its own.</summary>
    private static void SweepExpired(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM push_actions WHERE expires_at < $now;";
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }
}
