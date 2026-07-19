using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

using TheKrystalShip.Llm.Conversation;

namespace TheKrystalShip.Kgsm.Assistant.Service.PendingWrites;

/// <summary>
/// SQLite-backed <see cref="IPendingWriteStore"/>, sharing the SAME database FILE as the conversation
/// history (<see cref="ConversationOptions.DatabasePath"/>) rather than opening a second one — one state
/// file for the Service's durable data, one less path to configure/back up. Adds its own
/// <c>pending_writes</c> table (idempotent create, like <c>SqliteConversationStore</c>'s). SQLite is
/// single-writer, so writes are serialised through a process lock exactly like that store; WAL still lets
/// reads run concurrently. Being SQLite (not in-memory) means a staged write survives a Service restart
/// within the token TTL — the same restart-durability property the conversation store has.
/// </summary>
internal sealed class SqlitePendingWriteStore : IPendingWriteStore
{
    private readonly string _connectionString;
    private readonly object _writeGate = new();

    public SqlitePendingWriteStore(IOptions<ConversationOptions> options)
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
        // WAL is almost certainly already set by SqliteConversationStore on this same file, but the
        // pragma is idempotent and this store must not assume load order. The table create is
        // idempotent too — this store's own "EnsureCreated", independent of that store's schema.
        cmd.CommandText =
            """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS pending_writes (
                id         TEXT PRIMARY KEY,
                content    TEXT NOT NULL,
                expires_at TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public string Put(string content, DateTimeOffset expiry)
    {
        var id = Guid.NewGuid().ToString("N");
        lock (_writeGate)
        {
            using var connection = Open();

            SweepExpired(connection);

            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "INSERT INTO pending_writes (id, content, expires_at) VALUES ($id, $content, $expires);";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$content", content);
            cmd.Parameters.AddWithValue("$expires", expiry.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        return id;
    }

    public bool TryTake(string id, out string content)
    {
        content = string.Empty;
        if (string.IsNullOrWhiteSpace(id))
            return false;

        lock (_writeGate)
        {
            using var connection = Open();

            string? found = null;
            DateTimeOffset expiresAt = default;
            using (var select = connection.CreateCommand())
            {
                select.CommandText = "SELECT content, expires_at FROM pending_writes WHERE id = $id;";
                select.Parameters.AddWithValue("$id", id);
                using var reader = select.ExecuteReader();
                if (reader.Read())
                {
                    found = reader.GetString(0);
                    expiresAt = DateTimeOffset.Parse(reader.GetString(1));
                }
            }

            if (found is null)
                return false;

            // Single-use regardless of outcome: delete now so a peeked-but-expired row never lingers
            // for a second (successful or not) take.
            using (var delete = connection.CreateCommand())
            {
                delete.CommandText = "DELETE FROM pending_writes WHERE id = $id;";
                delete.Parameters.AddWithValue("$id", id);
                delete.ExecuteNonQuery();
            }

            if (expiresAt < DateTimeOffset.UtcNow)
                return false; // expired

            content = found;
            return true;
        }
    }

    /// <summary>Opportunistic cleanup of rows past their TTL, run inline with every <see cref="Put"/>
    /// (a confirmation is staged far less often than it's read, so no separate timer is needed).</summary>
    private static void SweepExpired(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM pending_writes WHERE expires_at < $now;";
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
