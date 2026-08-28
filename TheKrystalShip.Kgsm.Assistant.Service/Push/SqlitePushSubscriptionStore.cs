using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

using TheKrystalShip.KGSM.WebPush;
using TheKrystalShip.Llm.Conversation;

namespace TheKrystalShip.Kgsm.Assistant.Service.Push;

/// <inheritdoc/>
/// <remarks>
/// Two tables in the shared state file (see <see cref="StateDatabase"/>): the one-row VAPID identity
/// and the devices registered against it. Writes are serialised through a process lock, as every
/// other store on this file does — SQLite is single-writer and WAL leaves reads concurrent regardless.
/// </remarks>
internal sealed class SqlitePushSubscriptionStore : IPushSubscriptionStore
{
    private readonly string _connectionString;
    private readonly object _writeGate = new();

    // The pair never changes for the life of the host, so it is read once and held. Generating it is
    // the only write, and it happens at most once ever.
    private VapidKeyPair? _keys;

    // Retiring a device voids what was staged for it, and those handles are the action store's rows.
    // Reaching into its table from here would be two owners for one schema; asking it is one.
    private readonly IPushActionStore _actions;

    public SqlitePushSubscriptionStore(IOptions<ConversationOptions> options, IPushActionStore actions)
    {
        _connectionString = StateDatabase.ConnectionString(options.Value);
        _actions = actions;
        Initialize();
    }

    private void Initialize()
    {
        using var connection = StateDatabase.Open(_connectionString);
        using var cmd = connection.CreateCommand();
        // The CHECK pins the identity table to a single row: there is one sender on this host, and a
        // second pair appearing would orphan every device registered against the first.
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS push_vapid (
                id          INTEGER PRIMARY KEY CHECK (id = 1),
                public_key  TEXT NOT NULL,
                private_key TEXT NOT NULL,
                created_at  TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS push_subscriptions (
                endpoint   TEXT PRIMARY KEY,
                user_id    TEXT NOT NULL,
                p256dh     TEXT NOT NULL,
                auth       TEXT NOT NULL,
                origin     TEXT,
                created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_push_subscriptions_user
                ON push_subscriptions (user_id);
            """;
        cmd.ExecuteNonQuery();
    }

    public VapidKeyPair Keys()
    {
        if (_keys is not null)
            return _keys;

        lock (_writeGate)
        {
            if (_keys is not null)
                return _keys;

            using var connection = StateDatabase.Open(_connectionString);

            using (var select = connection.CreateCommand())
            {
                select.CommandText = "SELECT public_key, private_key FROM push_vapid WHERE id = 1;";
                using var reader = select.ExecuteReader();
                if (reader.Read())
                    return _keys = new VapidKeyPair(
                        PrivateKey: reader.GetString(1), PublicKey: reader.GetString(0));
            }

            var generated = VapidKeyPair.Generate();

            // OR IGNORE rather than INSERT: two processes racing the first push would otherwise have
            // one of them win and the other throw, where what is wanted is that the row created first
            // is the one everybody uses. The re-read below is what makes that true.
            using (var insert = connection.CreateCommand())
            {
                insert.CommandText =
                    """
                    INSERT OR IGNORE INTO push_vapid (id, public_key, private_key, created_at)
                    VALUES (1, $public, $private, $now);
                    """;
                // VapidKeyPair is positionally (PrivateKey, PublicKey). Every construction and read
                // of it here names its arguments, because getting the two the wrong way round produces
                // a pair that looks fine, stores fine, and signs nothing a push service will accept.
                insert.Parameters.AddWithValue("$public", generated.PublicKey);
                insert.Parameters.AddWithValue("$private", generated.PrivateKey);
                insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                insert.ExecuteNonQuery();
            }

            using (var confirm = connection.CreateCommand())
            {
                confirm.CommandText = "SELECT public_key, private_key FROM push_vapid WHERE id = 1;";
                using var reader = confirm.ExecuteReader();
                reader.Read();
                return _keys = new VapidKeyPair(
                    PrivateKey: reader.GetString(1), PublicKey: reader.GetString(0));
            }
        }
    }

    public void Register(string userId, PushSubscription subscription, string? origin)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        lock (_writeGate)
        {
            using var connection = StateDatabase.Open(_connectionString);
            using var cmd = connection.CreateCommand();
            // Re-registering is the ordinary case, not an error: the client re-posts whenever it finds
            // a live subscription the host may not know about, which is what heals a row lost to a
            // restored backup or a failed first POST.
            cmd.CommandText =
                """
                INSERT INTO push_subscriptions (endpoint, user_id, p256dh, auth, origin, created_at)
                VALUES ($endpoint, $user, $p256dh, $auth, $origin, $now)
                ON CONFLICT (endpoint) DO UPDATE SET
                    user_id = excluded.user_id,
                    p256dh  = excluded.p256dh,
                    auth    = excluded.auth,
                    origin  = excluded.origin;
                """;
            cmd.Parameters.AddWithValue("$endpoint", subscription.Endpoint);
            cmd.Parameters.AddWithValue("$user", userId);
            cmd.Parameters.AddWithValue("$p256dh", subscription.P256dh);
            cmd.Parameters.AddWithValue("$auth", subscription.Auth);
            cmd.Parameters.AddWithValue("$origin", (object?)origin ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    public bool Unregister(string userId, string endpoint)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(endpoint))
            return false;

        lock (_writeGate)
        {
            using var connection = StateDatabase.Open(_connectionString);
            using var cmd = connection.CreateCommand();
            // Scoped to the owner: an endpoint is not a handle on somebody else's device.
            cmd.CommandText =
                "DELETE FROM push_subscriptions WHERE endpoint = $endpoint AND user_id = $user;";
            cmd.Parameters.AddWithValue("$endpoint", endpoint);
            cmd.Parameters.AddWithValue("$user", userId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public IReadOnlyList<StoredSubscription> For(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return [];

        using var connection = StateDatabase.Open(_connectionString);
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT endpoint, p256dh, auth, origin FROM push_subscriptions
            WHERE user_id = $user ORDER BY created_at;
            """;
        cmd.Parameters.AddWithValue("$user", userId);

        var found = new List<StoredSubscription>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            found.Add(new StoredSubscription(
                new PushSubscription(reader.GetString(0), reader.GetString(1), reader.GetString(2)),
                userId,
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }
        return found;
    }

    public void Retire(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return;

        // The handles go first. A device with no subscription left is unreachable, so a handle that
        // outlived the row would be a live capability nothing could ever deliver again — and voiding
        // before deleting means a crash between the two leaves the harmless half undone.
        _actions.VoidFor(endpoint);

        lock (_writeGate)
        {
            using var connection = StateDatabase.Open(_connectionString);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM push_subscriptions WHERE endpoint = $endpoint;";
            cmd.Parameters.AddWithValue("$endpoint", endpoint);
            cmd.ExecuteNonQuery();
        }
    }
}
