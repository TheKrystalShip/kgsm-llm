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
    // A soft-delete tombstone. A conversation whose newest tombstone out-ids its newest content entry is
    // hidden from ListConversations, but every turn STAYS in the log — the corpus is never destroyed.
    // Append-only and latest-wins: a later turn (a resume) is newer than the tombstone → it un-hides.
    private const string KindDeleted = "deleted";

    // The recap wording prepended to a checkpoint summary when projected into the model's context, so
    // the model reads it as a compacted recap rather than a normal assistant message.
    private const string CheckpointPreamble =
        "(Summary of our conversation so far — earlier turns were compacted to save context.)\n\n";

    // Web defaults (camelCase + case-insensitive reads) with enums AS camelCase strings — the SAME shape
    // the live /turn SSE emits (SseTurnWriter). A §5·a card stored here is therefore byte-identical to the
    // one streamed live, so the reverse path (which re-emits the stored card verbatim) renders a replayed
    // card through the very same client path as a live one — the whole point of the §5·a alignment.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
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

    // The longest a derived title is kept (first prompt, single-lined). Slack over the ~40 the SPA shows.
    private const int TitleMaxLength = 80;

    public IReadOnlyList<ConversationSummary> ListConversations(string scopeKey, bool includeDeleted = false)
    {
        // Match the scope key itself (the bare per-user conversation) OR its ":"-children (per-chat ids).
        // scopeKey is surface:userId — neither segment carries a LIKE wildcard (% or _), so the pattern
        // is literal-safe without an ESCAPE clause.
        var childPattern = scopeKey + ":%";

        using var connection = Open();

        // One row per conversation: bounds, turn count, per-outcome tallies, and whether it is
        // soft-deleted. SUM(kind='turn') counts turns, excluding checkpoints; the outcome tallies read
        // the stored payload through json_extract (the payload IS the turn record, so no column and no
        // migration is needed for a field it already carries). Ordered most-recently-active first so
        // the surface's list reads newest-down.
        var summaries = new List<(string Id, DateTimeOffset Created, DateTimeOffset Last, int Turns,
            bool Deleted, int Errors, int CapHits, string? Display)>();
        using (var agg = connection.CreateCommand())
        {
            // Soft-deleted = the newest tombstone out-ids every content (turn/checkpoint) entry. A
            // resuming turn is newer than the tombstone → not deleted (latest-entry-wins). A
            // tombstone-only id (no content at all) counts as deleted. The owner's own list filters
            // these out; a review listing keeps them and flags each one.
            const string DeletedExpr =
                """
                MAX(CASE WHEN kind = $deleted THEN id ELSE 0 END)
                    > MAX(CASE WHEN kind <> $deleted THEN id ELSE 0 END)
                """;
            agg.CommandText =
                $"""
                SELECT conversation_id,
                       MIN(created_at) AS created,
                       MAX(created_at) AS last,
                       SUM(CASE WHEN kind = $turn THEN 1 ELSE 0 END) AS turns,
                       {DeletedExpr} AS is_deleted,
                       SUM(CASE WHEN kind = $turn AND json_extract(payload, '$.outcome') = 'error'
                                THEN 1 ELSE 0 END) AS errors,
                       SUM(CASE WHEN kind = $turn AND json_extract(payload, '$.outcome') = 'capHit'
                                THEN 1 ELSE 0 END) AS cap_hits,
                       -- The newest name any turn recorded; null when none carries one.
                       (SELECT json_extract(n.payload, '$.userDisplay')
                        FROM conversation_entries n
                        WHERE n.conversation_id = conversation_entries.conversation_id
                          AND n.kind = $turn
                          AND json_extract(n.payload, '$.userDisplay') IS NOT NULL
                        ORDER BY n.id DESC LIMIT 1) AS display
                FROM conversation_entries
                WHERE conversation_id = $scope OR conversation_id LIKE $child
                GROUP BY conversation_id
                HAVING $includeDeleted OR NOT ({DeletedExpr})
                ORDER BY last DESC;
                """;
            agg.Parameters.AddWithValue("$turn", KindTurn);
            agg.Parameters.AddWithValue("$deleted", KindDeleted);
            agg.Parameters.AddWithValue("$scope", scopeKey);
            agg.Parameters.AddWithValue("$child", childPattern);
            agg.Parameters.AddWithValue("$includeDeleted", includeDeleted ? 1 : 0);
            using var reader = agg.ExecuteReader();
            while (reader.Read())
            {
                summaries.Add((
                    reader.GetString(0),
                    DateTimeOffset.Parse(reader.GetString(1)),
                    DateTimeOffset.Parse(reader.GetString(2)),
                    reader.GetInt32(3),
                    reader.GetInt64(4) != 0,
                    reader.GetInt32(5),
                    reader.GetInt32(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7)));
            }
        }

        // Per conversation, the first turn's prompt → the title. Pull only the first turn's payload
        // (one small-ish row each) rather than the whole transcript: the lowest id of kind='turn'.
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var firsts = connection.CreateCommand())
        {
            firsts.CommandText =
                """
                SELECT e.conversation_id, e.payload
                FROM conversation_entries e
                JOIN (
                    SELECT conversation_id, MIN(id) AS first_turn_id
                    FROM conversation_entries
                    WHERE kind = $turn AND (conversation_id = $scope OR conversation_id LIKE $child)
                    GROUP BY conversation_id
                ) f ON e.id = f.first_turn_id;
                """;
            firsts.Parameters.AddWithValue("$turn", KindTurn);
            firsts.Parameters.AddWithValue("$scope", scopeKey);
            firsts.Parameters.AddWithValue("$child", childPattern);
            using var reader = firsts.ExecuteReader();
            while (reader.Read())
            {
                var turn = JsonSerializer.Deserialize<ConversationTurnRecord>(reader.GetString(1), Json);
                if (turn is not null)
                    titles[reader.GetString(0)] = DeriveTitle(turn.UserPrompt);
            }
        }

        return summaries.Select(s => new ConversationSummary
        {
            ConversationId = s.Id,
            Title = titles.TryGetValue(s.Id, out var t) ? t : null,
            CreatedAt = s.Created,
            LastActivityAt = s.Last,
            TurnCount = s.Turns,
            UserDisplay = s.Display,
            Deleted = s.Deleted,
            ErrorTurns = s.Errors,
            CapHitTurns = s.CapHits,
        }).ToList();
    }

    public IReadOnlyList<ConversationActor> ListActors(string surfacePrefix)
    {
        // The actor namespace is the id up to its SECOND ':' — {surface}:{user} — or the whole id when
        // it has no chat segment (the bare per-user conversation). Derived in SQL from the ids
        // themselves: the store holds no user table, and inventing one would mean a second source of
        // truth for who exists.
        var surface = surfacePrefix.TrimEnd(':');
        var pattern = surface + ":%";

        using var connection = Open();
        using var cmd = connection.CreateCommand();
        // actor = surface || ':' || <segment after the surface, up to the next ':' if there is one>.
        // instr() returns 0 when the remainder has no ':' (no chat segment) → take the whole remainder.
        const string ActorExpr =
            """
            $surface || ':' || (
                CASE WHEN instr(substr(conversation_id, length($surface) + 2), ':') = 0
                     THEN substr(conversation_id, length($surface) + 2)
                     ELSE substr(conversation_id, length($surface) + 2,
                                 instr(substr(conversation_id, length($surface) + 2), ':') - 1)
                END)
            """;
        // Per conversation first (deleted-ness is a per-conversation property — the newest tombstone
        // out-idding every content entry), then rolled up per actor. Doing it in one pass would count
        // a tombstone as if it were the conversation's state rather than the id's.
        cmd.CommandText =
            $"""
            WITH per_conversation AS (
                SELECT {ActorExpr} AS actor,
                       conversation_id,
                       MIN(created_at) AS created,
                       MAX(created_at) AS last,
                       SUM(CASE WHEN kind = $turn THEN 1 ELSE 0 END) AS turns,
                       MAX(CASE WHEN kind = $deleted THEN id ELSE 0 END)
                           > MAX(CASE WHEN kind <> $deleted THEN id ELSE 0 END) AS is_deleted
                FROM conversation_entries
                WHERE conversation_id = $surface OR conversation_id LIKE $pattern
                GROUP BY conversation_id
            )
            SELECT actor,
                   COUNT(*) AS conversations,
                   SUM(is_deleted) AS deleted,
                   SUM(turns) AS turns,
                   MIN(created) AS first_at,
                   MAX(last) AS last_at,
                   (SELECT json_extract(n.payload, '$.userDisplay')
                    FROM conversation_entries n
                    WHERE (n.conversation_id = per_conversation.actor
                           OR n.conversation_id LIKE per_conversation.actor || ':%')
                      AND n.kind = $turn
                      AND json_extract(n.payload, '$.userDisplay') IS NOT NULL
                    ORDER BY n.id DESC LIMIT 1) AS display
            FROM per_conversation
            GROUP BY actor
            ORDER BY last_at DESC;
            """;
        cmd.Parameters.AddWithValue("$turn", KindTurn);
        cmd.Parameters.AddWithValue("$deleted", KindDeleted);
        cmd.Parameters.AddWithValue("$surface", surface);
        cmd.Parameters.AddWithValue("$pattern", pattern);

        var actors = new List<ConversationActor>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var actor = reader.GetString(0);
            // actor is "{surface}:{user}"; the user segment is everything past the surface + ':'.
            var userId = actor.Length > surface.Length + 1 ? actor[(surface.Length + 1)..] : string.Empty;
            actors.Add(new ConversationActor
            {
                Surface = surface,
                UserId = userId,
                UserDisplay = reader.IsDBNull(6) ? null : reader.GetString(6),
                ConversationCount = reader.GetInt32(1),
                DeletedCount = reader.GetInt32(2),
                TurnCount = reader.GetInt32(3),
                FirstActivityAt = DateTimeOffset.Parse(reader.GetString(4)),
                LastActivityAt = DateTimeOffset.Parse(reader.GetString(5)),
            });
        }

        return actors;
    }

    // A conversation's display title: its first prompt, collapsed to a single line and length-capped.
    private static string DeriveTitle(string prompt)
    {
        var oneLine = prompt.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= TitleMaxLength ? oneLine : oneLine[..TitleMaxLength].TrimEnd() + "…";
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

    public void SoftDelete(string conversationId)
    {
        // Append-only tombstone: hides the conversation from ListConversations while keeping every turn in
        // the log. The payload is empty — the marker's kind and position (newest id) are all that matter.
        Insert(conversationId, KindDeleted, DateTimeOffset.UtcNow, string.Empty);
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

            if (kind == KindDeleted)
            {
                // A soft-delete tombstone is bookkeeping, not content — never surfaced in the transcript
                // (and its payload is empty, so it must not reach the turn deserializer below).
                continue;
            }
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
