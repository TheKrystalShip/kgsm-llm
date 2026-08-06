using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

using TheKrystalShip.KGSM.Auth.Sessions;
using TheKrystalShip.Llm.Conversation;

namespace TheKrystalShip.Kgsm.Assistant.Service.Security;

/// <summary>
/// SQLite-backed <see cref="ISessionRegistry"/>, sharing the SAME database FILE as the conversation
/// history (<see cref="ConversationOptions.DatabasePath"/>) rather than opening a second one — one
/// state file for the Service's durable data, exactly as <c>SqlitePendingWriteStore</c> does. Adds
/// its own <c>sessions</c> table with an idempotent create.
/// </summary>
/// <remarks>
/// <para>
/// Durability is the point: a session that lives only in memory dies with the process, so every
/// restart logs everyone out and no revocation can outlive the thing it revoked. A row on disk makes
/// "this login is dead" a fact that survives both.
/// </para>
/// <para>
/// SQLite is single-writer, so writes serialise through a process lock like the sibling stores; WAL
/// still lets reads run concurrently. Reads go unlocked — the validator's cache means they are the
/// hot path, and a read racing a write sees one side or the other of a single-row update, never a
/// torn one.
/// </para>
/// <para>
/// Timestamps are stored as ISO-8601 round-trip strings ("O"), which sort lexicographically in the
/// same order they sort chronologically — so the expiry comparisons below are plain string compares
/// and still mean what they say.
/// </para>
/// </remarks>
internal sealed class SqliteSessionRegistry : ISessionRegistry
{
    private readonly string _connectionString;
    private readonly Lock _writeGate = new();

    public SqliteSessionRegistry(IOptions<ConversationOptions> options)
    {
        string? configured = options.Value.DatabasePath;

        // Same default-path rule as SqliteConversationStore: a configured path wins, otherwise a file
        // beside the host binary — this store always shares whichever file that store picked.
        string databasePath = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "conversations.db")
            : configured;

        string? directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
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
        using SqliteConnection connection = Open();
        using SqliteCommand cmd = connection.CreateCommand();
        // WAL is almost certainly already set by SqliteConversationStore on this same file, but the
        // pragma is idempotent and this store must not assume load order.
        cmd.CommandText =
            """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS sessions (
                session_id  TEXT PRIMARY KEY,
                user_id     TEXT NOT NULL,
                host_id     TEXT NOT NULL,
                created     TEXT NOT NULL,
                expires     TEXT NOT NULL,
                user_agent  TEXT NULL,
                current_jti TEXT NULL,
                revoked     INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_sessions_user ON sessions (user_id);
            """;
        cmd.ExecuteNonQuery();
    }

    public Task CreateAsync(SessionRegistration session, CancellationToken ct = default)
    {
        lock (_writeGate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO sessions
                    (session_id, user_id, host_id, created, expires, user_agent, current_jti, revoked)
                VALUES ($sid, $user, $host, $created, $expires, $ua, $jti, 0);
                """;
            cmd.Parameters.AddWithValue("$sid", session.SessionId);
            cmd.Parameters.AddWithValue("$user", session.UserId);
            cmd.Parameters.AddWithValue("$host", session.HostId);
            cmd.Parameters.AddWithValue("$created", session.Created.ToString("O"));
            cmd.Parameters.AddWithValue("$expires", session.Expires.ToString("O"));
            cmd.Parameters.AddWithValue("$ua", (object?)session.UserAgent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$jti", (object?)session.CurrentJti ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        return Task.CompletedTask;
    }

    public Task<bool> IsAliveAsync(string sessionId, CancellationToken ct = default)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT 1 FROM sessions WHERE session_id = $sid AND revoked = 0 AND expires > $now;";
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));

        return Task.FromResult(cmd.ExecuteScalar() is not null);
    }

    /// <summary>
    /// Rotates the refresh token, sliding the absolute expiry forward. The <c>current_jti</c> match is
    /// the reuse detection and it is part of the UPDATE's own WHERE clause rather than a read followed
    /// by a write — two refreshes racing with the same token would both pass a separate check, and only
    /// one may win.
    /// </summary>
    public Task<bool> RotateAsync(
        string sessionId, string presentedJti, string newJti, DateTimeOffset newExpires,
        CancellationToken ct = default)
    {
        lock (_writeGate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                UPDATE sessions
                   SET current_jti = $new, expires = $expires
                 WHERE session_id = $sid
                   AND current_jti = $presented
                   AND revoked = 0
                   AND expires > $now;
                """;
            cmd.Parameters.AddWithValue("$sid", sessionId);
            cmd.Parameters.AddWithValue("$presented", presentedJti);
            cmd.Parameters.AddWithValue("$new", newJti);
            cmd.Parameters.AddWithValue("$expires", newExpires.ToString("O"));
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));

            return Task.FromResult(cmd.ExecuteNonQuery() == 1);
        }
    }

    /// <summary>
    /// Marks a session revoked rather than deleting the row: the GC sweep clears it once it is past
    /// its cap anyway, and until then the tombstone is what a replayed refresh lands on.
    /// </summary>
    public Task<bool> RevokeAsync(string sessionId, CancellationToken ct = default)
    {
        lock (_writeGate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText =
                "UPDATE sessions SET revoked = 1, current_jti = NULL WHERE session_id = $sid AND revoked = 0;";
            cmd.Parameters.AddWithValue("$sid", sessionId);

            return Task.FromResult(cmd.ExecuteNonQuery() == 1);
        }
    }

    public Task<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        lock (_writeGate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM sessions WHERE expires <= $now;";
            cmd.Parameters.AddWithValue("$now", now.ToString("O"));

            return Task.FromResult(cmd.ExecuteNonQuery());
        }
    }

    /// <summary>
    /// The host a session was recorded against, or null. It mirrors the audience its tokens were
    /// minted under, so the two are checkable against each other rather than merely believed equal.
    /// </summary>
    public string? HostOf(string sessionId)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT host_id FROM sessions WHERE session_id = $sid;";
        cmd.Parameters.AddWithValue("$sid", sessionId);

        return cmd.ExecuteScalar() as string;
    }

    /// <summary>The user a live session belongs to, or null. Used to scope a self-revoke.</summary>
    public string? UserOf(string sessionId)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT user_id FROM sessions WHERE session_id = $sid AND revoked = 0;";
        cmd.Parameters.AddWithValue("$sid", sessionId);

        return cmd.ExecuteScalar() as string;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
