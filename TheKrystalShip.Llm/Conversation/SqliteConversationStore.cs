using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Llm.Conversation;

/// <summary>
/// The conversation store: a single SQLite file holding each conversation's rolling working context, so
/// it survives a process restart/redeploy. Keyed by the opaque conversation id, oldest first, trimmed to
/// <see cref="ConversationOptions.MaxMessages"/>.
/// <para>
/// The conversation id is the canonical scope — a fresh chat is a fresh id — so there is no idle reset:
/// a conversation is retained and resumable by id until it rolls out of its own window or is replaced.
/// Each call opens a pooled connection; writes are serialised through a process lock (SQLite is
/// single-writer) while WAL lets reads run concurrently. Each <see cref="LlmMessage"/> is one JSON row
/// (roles as strings so the enum can be reordered without breaking old rows).
/// </para>
/// </summary>
public sealed class SqliteConversationStore : IConversationStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _connectionString;
    private readonly int _maxMessages;
    // SQLite is single-writer; serialise writes so concurrent turns never hit "database is locked".
    private readonly object _writeGate = new();

    public SqliteConversationStore(IOptions<ConversationOptions> options)
    {
        var value = options.Value;
        _maxMessages = Math.Max(1, value.MaxMessages);

        // A configured path wins; otherwise default beside the host binary so the store always has a
        // home (the deployed Service points this at its state dir via Conversation:DatabasePath).
        var databasePath = string.IsNullOrWhiteSpace(value.DatabasePath)
            ? Path.Combine(AppContext.BaseDirectory, "conversations.db")
            : value.DatabasePath;

        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate, // created on first open if missing
            DefaultTimeout = 5,                     // wait briefly under a transient writer lock
        }.ToString();

        Initialize();
    }

    private void Initialize()
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        // WAL: durable across restarts and lets a read run while a write is in flight. The schema is
        // idempotent (CREATE IF NOT EXISTS) — the store's "EnsureCreated".
        cmd.CommandText =
            """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS conversation_messages (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                conversation_id TEXT    NOT NULL,
                payload         TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_conversation
                ON conversation_messages (conversation_id, id);
            """;
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<LlmMessage> GetHistory(string conversationId)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT payload FROM conversation_messages WHERE conversation_id = $cid ORDER BY id ASC;";
        cmd.Parameters.AddWithValue("$cid", conversationId);

        var history = new List<LlmMessage>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var message = JsonSerializer.Deserialize<LlmMessage>(reader.GetString(0), Json);
            if (message is not null)
                history.Add(message);
        }

        return history;
    }

    public void Append(string conversationId, params LlmMessage[] messages)
    {
        if (messages.Length == 0)
            return;

        lock (_writeGate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            foreach (var message in messages)
                Insert(connection, tx, conversationId, message);
            Trim(connection, tx, conversationId);
            tx.Commit();
        }
    }

    public void Replace(string conversationId, params LlmMessage[] messages)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = tx;
                delete.CommandText = "DELETE FROM conversation_messages WHERE conversation_id = $cid;";
                delete.Parameters.AddWithValue("$cid", conversationId);
                delete.ExecuteNonQuery();
            }

            foreach (var message in messages)
                Insert(connection, tx, conversationId, message);
            Trim(connection, tx, conversationId);
            tx.Commit();
        }
    }

    private static void Insert(SqliteConnection connection, SqliteTransaction tx, string conversationId, LlmMessage message)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO conversation_messages (conversation_id, payload) VALUES ($cid, $payload);";
        cmd.Parameters.AddWithValue("$cid", conversationId);
        cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(message, Json));
        cmd.ExecuteNonQuery();
    }

    // Keep only the newest _maxMessages rows for this conversation (the rolling window) — bounds the file
    // so durability doesn't become unbounded growth.
    private void Trim(SqliteConnection connection, SqliteTransaction tx, string conversationId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            DELETE FROM conversation_messages
            WHERE conversation_id = $cid
              AND id NOT IN (
                  SELECT id FROM conversation_messages
                  WHERE conversation_id = $cid
                  ORDER BY id DESC
                  LIMIT $keep
              );
            """;
        cmd.Parameters.AddWithValue("$cid", conversationId);
        cmd.Parameters.AddWithValue("$keep", _maxMessages);
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
