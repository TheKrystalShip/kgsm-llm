using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Llm.Conversation;

/// <summary>
/// The canonical conversation history in a single SQLite file: an append-only log of per-turn deltas
/// and compaction checkpoints (<c>conversation_entries</c>). It is BOTH the model's continuity memory
/// and the durable, examinable record — never trimmed, never overwritten. Full history survives a
/// restart and is resumable by conversation id.
/// <para>
/// What the model replays is a projection (<see cref="GetModelContext"/>): the latest checkpoint
/// summary plus the user/assistant text of the turns after it. Compaction is non-destructive — it
/// appends a checkpoint, leaving prior turns intact. Each call opens a pooled connection; writes are
/// serialised through a process lock (SQLite is single-writer) while WAL lets reads run concurrently.
/// </para>
/// </summary>
public sealed class SqliteConversationStore : IConversationStore
{
    private const string KindTurn = "turn";
    private const string KindCheckpoint = "checkpoint";

    // The recap wording prepended to a checkpoint summary when projected into the model's context, so
    // the model reads it as a compacted recap rather than a normal assistant message.
    private const string CheckpointPreamble =
        "(Summary of our conversation so far — earlier turns were compacted to save context.)\n\n";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _connectionString;
    // SQLite is single-writer; serialise writes so concurrent turns never hit "database is locked".
    private readonly object _writeGate = new();

    public SqliteConversationStore(IOptions<ConversationOptions> options)
    {
        var value = options.Value;

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
            CREATE TABLE IF NOT EXISTS conversation_entries (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                conversation_id TEXT    NOT NULL,
                kind            TEXT    NOT NULL,
                created_at      TEXT    NOT NULL,
                payload         TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_conversation
                ON conversation_entries (conversation_id, id);
            """;
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<ConversationEntry> GetHistory(string conversationId) =>
        LoadEntries(conversationId);

    public IReadOnlyList<LlmMessage> GetModelContext(string conversationId)
    {
        var entries = LoadEntries(conversationId);

        // Replay from the latest checkpoint forward (or the whole conversation if there is none).
        var lastCheckpoint = -1;
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i].Kind == ConversationEntryKind.Checkpoint)
            {
                lastCheckpoint = i;
                break;
            }
        }

        var messages = new List<LlmMessage>();
        var start = 0;
        if (lastCheckpoint >= 0)
        {
            messages.Add(LlmMessage.Assistant(CheckpointPreamble + entries[lastCheckpoint].CheckpointSummary));
            start = lastCheckpoint + 1;
        }

        for (var i = start; i < entries.Count; i++)
        {
            if (entries[i].Kind != ConversationEntryKind.Turn)
                continue;
            var turn = entries[i].Turn!;
            messages.Add(LlmMessage.User(turn.UserPrompt));
            if (!string.IsNullOrWhiteSpace(turn.Final))
                messages.Add(LlmMessage.Assistant(turn.Final!));
        }

        return messages;
    }

    public void AppendTurn(ConversationTurnRecord turn)
    {
        Insert(turn.ConversationId, KindTurn, turn.CompletedAt, JsonSerializer.Serialize(turn, Json));
    }

    public void AddCheckpoint(string conversationId, string summary)
    {
        Insert(conversationId, KindCheckpoint, DateTimeOffset.UtcNow, summary);
    }

    private void Insert(string conversationId, string kind, DateTimeOffset createdAt, string payload)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO conversation_entries (conversation_id, kind, created_at, payload)
                VALUES ($cid, $kind, $createdAt, $payload);
                """;
            cmd.Parameters.AddWithValue("$cid", conversationId);
            cmd.Parameters.AddWithValue("$kind", kind);
            cmd.Parameters.AddWithValue("$createdAt", createdAt.ToString("O"));
            cmd.Parameters.AddWithValue("$payload", payload);
            cmd.ExecuteNonQuery();
        }
    }

    private List<ConversationEntry> LoadEntries(string conversationId)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT kind, created_at, payload FROM conversation_entries WHERE conversation_id = $cid ORDER BY id ASC;";
        cmd.Parameters.AddWithValue("$cid", conversationId);

        var entries = new List<ConversationEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var kind = reader.GetString(0);
            var createdAt = DateTimeOffset.Parse(reader.GetString(1));
            var payload = reader.GetString(2);

            if (kind == KindCheckpoint)
            {
                entries.Add(ConversationEntry.ForCheckpoint(payload, createdAt));
            }
            else
            {
                var turn = JsonSerializer.Deserialize<ConversationTurnRecord>(payload, Json);
                if (turn is not null)
                    entries.Add(ConversationEntry.ForTurn(turn));
            }
        }

        return entries;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
