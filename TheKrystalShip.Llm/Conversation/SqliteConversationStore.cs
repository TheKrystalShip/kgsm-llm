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
    // The owner's verdict on ONE turn, carried as {turnId, rating, note} and resolved latest-wins — the
    // same append-only shape as the tombstone above. Re-rating and un-rating are both just appends, so a
    // rated turn's own record is never rewritten, and the corpus keeps the fact that a verdict changed.
    private const string KindFeedback = "feedback";
    // A per-conversation preference the next turn reads (thinking, auto-run), carried as a DELTA and
    // resolved latest-wins per field — the same append-only shape as the tombstone and the verdict, so
    // setting one is an append and the log keeps the fact that it changed.
    private const string KindPreference = "preference";
    // A conversation brought into being before it holds a turn, so "start a fresh conversation" is
    // something the leaf DID rather than an id a client is holding on to. Content, not bookkeeping: it
    // is what makes an empty conversation exist and list.
    private const string KindCreated = "created";

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

    // The actor namespace of a conversation id: everything up to its SECOND ':' — {surface}:{user} —
    // or the whole id when it carries no chat segment (the bare per-user conversation). Derived from
    // the ids themselves because the store holds no user table, and inventing one would create a
    // second source of truth for who exists. Expects a $surface parameter; instr() returns 0 when the
    // remainder has no ':', which is the no-chat-segment case.
    private const string ActorSql =
        """
        $surface || ':' || (
            CASE WHEN instr(substr(conversation_id, length($surface) + 2), ':') = 0
                 THEN substr(conversation_id, length($surface) + 2)
                 ELSE substr(conversation_id, length($surface) + 2,
                             instr(substr(conversation_id, length($surface) + 2), ':') - 1)
            END)
        """;

    // A verdict and a preference are both bookkeeping ABOUT a conversation, not activity IN it, so every
    // aggregate that treats an entry as the conversation happening has to skip them: rating an old chat
    // — or flipping its thinking switch — must not bump it to the top of its owner's list, and neither
    // must out-id a tombstone and resurrect a hidden chat.
    private const string NotBookkeepingSql = "kind NOT IN ($feedback, $preference)";

    // Whether an id holds anything but bookkeeping. Flipping a switch on a chat that was never started
    // writes a preference and nothing else, and such an id is not a conversation: it has no beginning
    // and no activity, so every aggregate over the log has to skip it rather than report one whose
    // timestamps are null. Grouped queries carry this in their HAVING.
    private const string HasContentSql =
        $"MAX(CASE WHEN {NotBookkeepingSql} THEN id ELSE 0 END) > 0";

    // The verdict that stands for each rated turn: the newest feedback row per turn id, since re-rating
    // appends rather than rewrites. A cleared verdict is the newest row too and carries a null rating,
    // which is how un-rating resolves back to "no verdict". Turn ids are log-wide unique, so partitioning
    // by the id alone is enough. Expects $feedback plus a scope predicate supplied by the caller.
    private const string LatestVerdictSql =
        """
        SELECT conversation_id,
               json_extract(payload, '$.turnId') AS turn_id,
               json_extract(payload, '$.rating') AS rating,
               json_extract(payload, '$.note')   AS note,
               created_at                        AS rated_at,
               ROW_NUMBER() OVER (PARTITION BY json_extract(payload, '$.turnId') ORDER BY id DESC) AS rn
        FROM conversation_entries
        WHERE kind = $feedback
        """;

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
            bool Deleted, int Errors, int CapHits, string? Display, bool? Think, bool? Autorun)>();
        using (var agg = connection.CreateCommand())
        {
            // The newest entry that said anything about one switch — the same latest-non-null-wins fold
            // GetPreferences does, expressed in SQL so a listing answers for every conversation in the
            // one pass. A delta says nothing about the switch it leaves null, so those rows are skipped
            // rather than read as "off".
            static string StandingSwitch(string field) =>
                $"""
                (SELECT json_extract(p.payload, '$.{field}')
                 FROM conversation_entries p
                 WHERE p.conversation_id = conversation_entries.conversation_id
                   AND p.kind = $preference
                   AND json_extract(p.payload, '$.{field}') IS NOT NULL
                 ORDER BY p.id DESC LIMIT 1)
                """;

            // Soft-deleted = the newest tombstone out-ids every content (turn/checkpoint) entry. A
            // resuming turn is newer than the tombstone → not deleted (latest-entry-wins). A
            // tombstone-only id (no content at all) counts as deleted. The owner's own list filters
            // these out; a review listing keeps them and flags each one.
            const string DeletedExpr =
                $"""
                MAX(CASE WHEN kind = $deleted THEN id ELSE 0 END)
                    > MAX(CASE WHEN kind <> $deleted AND {NotBookkeepingSql} THEN id ELSE 0 END)
                """;
            agg.CommandText =
                $"""
                SELECT conversation_id,
                       MIN(CASE WHEN {NotBookkeepingSql} THEN created_at END) AS created,
                       MAX(CASE WHEN {NotBookkeepingSql} THEN created_at END) AS last,
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
                        ORDER BY n.id DESC LIMIT 1) AS display,
                       {StandingSwitch("think")} AS think,
                       {StandingSwitch("autorun")} AS autorun
                FROM conversation_entries
                WHERE conversation_id = $scope OR conversation_id LIKE $child
                GROUP BY conversation_id
                HAVING ({HasContentSql}) AND ($includeDeleted OR NOT ({DeletedExpr}))
                ORDER BY last DESC;
                """;
            agg.Parameters.AddWithValue("$turn", KindTurn);
            agg.Parameters.AddWithValue("$deleted", KindDeleted);
            agg.Parameters.AddWithValue("$feedback", KindFeedback);
            agg.Parameters.AddWithValue("$preference", KindPreference);
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
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetInt64(8) != 0,
                    reader.IsDBNull(9) ? null : reader.GetInt64(9) != 0));
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

        // Per conversation, how many turns their owner marked as unhelpful — what makes a conversation
        // worth reading in a review listing. Counted from the verdict that STANDS for each turn, so a
        // thumbs-down later changed to a thumbs-up (or cleared) stops counting.
        var negatives = new Dictionary<string, int>(StringComparer.Ordinal);
        using (var down = connection.CreateCommand())
        {
            down.CommandText =
                $"""
                SELECT conversation_id, COUNT(*)
                FROM ({LatestVerdictSql} AND (conversation_id = $scope OR conversation_id LIKE $child))
                WHERE rn = 1 AND rating = 'down'
                GROUP BY conversation_id;
                """;
            down.Parameters.AddWithValue("$feedback", KindFeedback);
            down.Parameters.AddWithValue("$scope", scopeKey);
            down.Parameters.AddWithValue("$child", childPattern);
            using var reader = down.ExecuteReader();
            while (reader.Read())
                negatives[reader.GetString(0)] = reader.GetInt32(1);
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
            NegativeTurns = negatives.TryGetValue(s.Id, out var n) ? n : 0,
            Preferences = new ConversationPreferences(s.Think, s.Autorun),
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
        // Per conversation first (deleted-ness is a per-conversation property — the newest tombstone
        // out-idding every content entry), then rolled up per actor. Doing it in one pass would count
        // a tombstone as if it were the conversation's state rather than the id's.
        cmd.CommandText =
            $"""
            WITH per_conversation AS (
                SELECT {ActorSql} AS actor,
                       conversation_id,
                       MIN(CASE WHEN {NotBookkeepingSql} THEN created_at END) AS created,
                       MAX(CASE WHEN {NotBookkeepingSql} THEN created_at END) AS last,
                       SUM(CASE WHEN kind = $turn THEN 1 ELSE 0 END) AS turns,
                       MAX(CASE WHEN kind = $deleted THEN id ELSE 0 END)
                           > MAX(CASE WHEN kind <> $deleted AND {NotBookkeepingSql} THEN id ELSE 0 END) AS is_deleted
                FROM conversation_entries
                WHERE conversation_id = $surface OR conversation_id LIKE $pattern
                GROUP BY conversation_id
                HAVING {HasContentSql}
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
        cmd.Parameters.AddWithValue("$feedback", KindFeedback);
        cmd.Parameters.AddWithValue("$preference", KindPreference);
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

    public ConversationStats GetStats(string surfacePrefix)
    {
        var surface = surfacePrefix.TrimEnd(':');
        var pattern = surface + ":%";

        using var connection = Open();

        // ── Conversation-level shape: how many, how many hidden, how many distinct actors. Same
        // per-conversation-then-roll-up split as ListActors, and for the same reason: deleted-ness is a
        // property of the id's newest entry, so collapsing both levels into one pass would count a
        // tombstone as though it were the conversation's own state.
        int conversations = 0, deleted = 0, actors = 0;
        using (var shape = connection.CreateCommand())
        {
            shape.CommandText =
                $"""
                WITH per_conversation AS (
                    SELECT conversation_id,
                           {ActorSql} AS actor,
                           MAX(CASE WHEN kind = $deleted THEN id ELSE 0 END)
                               > MAX(CASE WHEN kind <> $deleted AND {NotBookkeepingSql} THEN id ELSE 0 END) AS is_deleted
                    FROM conversation_entries
                    WHERE conversation_id = $surface OR conversation_id LIKE $pattern
                    GROUP BY conversation_id
                    HAVING {HasContentSql}
                )
                SELECT COUNT(*), SUM(is_deleted), COUNT(DISTINCT actor) FROM per_conversation;
                """;
            shape.Parameters.AddWithValue("$deleted", KindDeleted);
            shape.Parameters.AddWithValue("$feedback", KindFeedback);
            shape.Parameters.AddWithValue("$preference", KindPreference);
            shape.Parameters.AddWithValue("$surface", surface);
            shape.Parameters.AddWithValue("$pattern", pattern);
            using var reader = shape.ExecuteReader();
            if (reader.Read())
            {
                conversations = reader.GetInt32(0);
                deleted = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                actors = reader.GetInt32(2);
            }
        }

        // ── One row per turn, with the scalars the roll-up needs pulled straight out of the payload.
        // Durations and percentiles are finished in memory: SQLite has no percentile aggregate, and the
        // ordered lists are needed anyway. Bounded by the corpus, which is the same bound ListActors
        // already accepts — when it stops holding, the fix is a cursor here, not a schema change.
        var durations = new List<long>();
        var iterations = new List<int>();
        var contextPercents = new List<double>();
        var windows = new HashSet<int>();
        int turns = 0, ok = 0, error = 0, capHit = 0, cancelled = 0, unrecorded = 0, thinking = 0, toolless = 0;
        var byPrompt = new Dictionary<string, (int Turns, int Ok, List<long> Durations, int Rated, int Negative)>(StringComparer.Ordinal);
        var byDay = new SortedDictionary<string, int>(StringComparer.Ordinal);
        // A prompt hash is nullable, and a dictionary cannot key on null — this stands in for "no hash
        // recorded" and is mapped back to null on the way out.
        const string NoHash = "\0none";

        // The verdict standing on each rated turn, read BEFORE the turns themselves so the per-turn pass
        // can attribute each one to its prompt-version bucket in the same sweep. Only judged turns appear
        // here: an unrated turn is absent, never a neutral entry.
        var verdicts = new Dictionary<long, (TurnFeedbackRating Rating, string? Note, DateTimeOffset At, string ConversationId)>();
        using (var judged = connection.CreateCommand())
        {
            judged.CommandText =
                $"""
                SELECT turn_id, rating, note, rated_at, conversation_id
                FROM ({LatestVerdictSql} AND (conversation_id = $surface OR conversation_id LIKE $pattern))
                WHERE rn = 1 AND rating IS NOT NULL;
                """;
            judged.Parameters.AddWithValue("$feedback", KindFeedback);
            judged.Parameters.AddWithValue("$surface", surface);
            judged.Parameters.AddWithValue("$pattern", pattern);
            using var reader = judged.ExecuteReader();
            while (reader.Read())
            {
                verdicts[reader.GetInt64(0)] = (
                    reader.GetString(1) == "down" ? TurnFeedbackRating.Down : TurnFeedbackRating.Up,
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    DateTimeOffset.Parse(reader.GetString(3)),
                    reader.GetString(4));
            }
        }

        int ratedTurns = 0, positiveTurns = 0, negativeTurns = 0;
        var notes = new List<FeedbackNote>();

        using (var perTurn = connection.CreateCommand())
        {
            perTurn.CommandText =
                $"""
                SELECT json_extract(payload, '$.outcome'),
                       json_extract(payload, '$.iterations'),
                       json_extract(payload, '$.think'),
                       json_extract(payload, '$.systemPromptHash'),
                       json_extract(payload, '$.startedAt'),
                       json_extract(payload, '$.completedAt'),
                       json_extract(payload, '$.usage.usedTokens'),
                       json_extract(payload, '$.usage.contextWindow'),
                       json_array_length(payload, '$.tools'),
                       id,
                       json_extract(payload, '$.userPrompt')
                FROM conversation_entries
                WHERE kind = $turn AND (conversation_id = $surface OR conversation_id LIKE $pattern);
                """;
            perTurn.Parameters.AddWithValue("$turn", KindTurn);
            perTurn.Parameters.AddWithValue("$surface", surface);
            perTurn.Parameters.AddWithValue("$pattern", pattern);
            using var reader = perTurn.ExecuteReader();
            while (reader.Read())
            {
                turns++;

                var outcome = reader.IsDBNull(0) ? null : reader.GetString(0);
                switch (outcome)
                {
                    case "ok": ok++; break;
                    case "error": error++; break;
                    case "capHit": capHit++; break;
                    case "cancelled": cancelled++; break;
                    // Recorded before the field existed. Counted on its own rather than assumed to be a
                    // success — assuming would inflate the clean rate this whole view is judged on.
                    default: unrecorded++; break;
                }

                if (!reader.IsDBNull(1)) iterations.Add(reader.GetInt32(1));
                if (!reader.IsDBNull(2) && reader.GetInt64(2) != 0) thinking++;

                var hash = reader.IsDBNull(3) ? NoHash : reader.GetString(3);

                long? durationMs = null;
                if (!reader.IsDBNull(4) && !reader.IsDBNull(5)
                    && DateTimeOffset.TryParse(reader.GetString(4), out var started)
                    && DateTimeOffset.TryParse(reader.GetString(5), out var completed)
                    && completed >= started)
                {
                    durationMs = (long)(completed - started).TotalMilliseconds;
                    durations.Add(durationMs.Value);
                }

                if (!reader.IsDBNull(4) && DateTimeOffset.TryParse(reader.GetString(4), out var day))
                {
                    var key = day.UtcDateTime.ToString("yyyy-MM-dd");
                    byDay[key] = byDay.TryGetValue(key, out var n) ? n + 1 : 1;
                }

                if (!reader.IsDBNull(6) && !reader.IsDBNull(7))
                {
                    var window = reader.GetInt32(7);
                    if (window > 0)
                    {
                        contextPercents.Add(reader.GetInt32(6) * 100.0 / window);
                        windows.Add(window);
                    }
                }

                if (reader.IsDBNull(8) || reader.GetInt32(8) == 0) toolless++;

                // The verdict its owner left, if any. Rolled up whole-corpus AND into this turn's prompt
                // bucket, which is what makes "did that prompt edit help?" answerable from the same read.
                var judgement = verdicts.TryGetValue(reader.GetInt64(9), out var v)
                    ? (TurnFeedbackRating?)v.Rating
                    : null;
                if (judgement is not null)
                {
                    ratedTurns++;
                    if (judgement == TurnFeedbackRating.Down) negativeTurns++; else positiveTurns++;
                    if (judgement == TurnFeedbackRating.Down && !string.IsNullOrWhiteSpace(v.Note))
                    {
                        notes.Add(new FeedbackNote
                        {
                            ConversationId = v.ConversationId,
                            TurnId = reader.GetInt64(9),
                            Note = v.Note!,
                            Prompt = reader.IsDBNull(10) ? null : DeriveTitle(reader.GetString(10)),
                            At = v.At,
                        });
                    }
                }

                if (!byPrompt.TryGetValue(hash, out var bucket))
                    bucket = (0, 0, new List<long>(), 0, 0);
                if (durationMs.HasValue) bucket.Durations.Add(durationMs.Value);
                byPrompt[hash] = (
                    bucket.Turns + 1,
                    bucket.Ok + (outcome == "ok" ? 1 : 0),
                    bucket.Durations,
                    bucket.Rated + (judgement is not null ? 1 : 0),
                    bucket.Negative + (judgement == TurnFeedbackRating.Down ? 1 : 0));
            }
        }

        // ── Tools, exploded out of each turn's trajectory array with json_each. One row per CALL, so a
        // turn that used the same tool twice contributes two — which is what "how often is it reached
        // for" means.
        var toolCalls = new Dictionary<string, (int Calls, List<long> Durations, int Failed)>(StringComparer.Ordinal);
        using (var tools = connection.CreateCommand())
        {
            tools.CommandText =
                """
                SELECT json_extract(t.value, '$.name.name'),
                       json_extract(t.value, '$.durationMs'),
                       json_extract(t.value, '$.summary')
                FROM conversation_entries e, json_each(e.payload, '$.tools') t
                WHERE e.kind = $turn AND (e.conversation_id = $surface OR e.conversation_id LIKE $pattern);
                """;
            tools.Parameters.AddWithValue("$turn", KindTurn);
            tools.Parameters.AddWithValue("$surface", surface);
            tools.Parameters.AddWithValue("$pattern", pattern);
            using var reader = tools.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0)) continue;
                var name = reader.GetString(0);
                if (!toolCalls.TryGetValue(name, out var bucket))
                    bucket = (0, new List<long>(), 0);
                // Every exploded row is a call. The duration list is separate because a refused call
                // never reached the dispatcher and carries none — counting only timed calls would drop
                // exactly the ones a review most wants to see.
                if (!reader.IsDBNull(1)) bucket.Durations.Add(reader.GetInt64(1));
                // The dispatcher contract: a failed call's model-facing output is an "Error: …" string
                // (ToolOutput). Reading the recorded output is a measurement; nothing else in the log
                // says whether a call worked.
                var failed = !reader.IsDBNull(2)
                    && reader.GetString(2).StartsWith("Error:", StringComparison.Ordinal);
                toolCalls[name] = (bucket.Calls + 1, bucket.Durations, bucket.Failed + (failed ? 1 : 0));
            }
        }

        var toolStats = toolCalls
            .Select(kv => new ToolStat
            {
                Name = kv.Key,
                Calls = kv.Value.Calls,
                MedianMs = Percentile(kv.Value.Durations, 50) ?? 0,
                MaxMs = kv.Value.Durations.Count > 0 ? kv.Value.Durations.Max() : 0,
                FailedCalls = kv.Value.Failed,
            })
            .OrderByDescending(t => t.Calls)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        return new ConversationStats
        {
            Conversations = conversations,
            DeletedConversations = deleted,
            Actors = actors,
            Turns = turns,
            OkTurns = ok,
            ErrorTurns = error,
            CapHitTurns = capHit,
            CancelledTurns = cancelled,
            UnrecordedOutcomeTurns = unrecorded,
            MedianTurnMs = Percentile(durations, 50),
            P95TurnMs = Percentile(durations, 95),
            MaxTurnMs = durations.Count > 0 ? durations.Max() : null,
            MedianIterations = iterations.Count > 0 ? (int)Percentile(iterations.Select(i => (long)i).ToList(), 50)!.Value : null,
            MaxIterations = iterations.Count > 0 ? iterations.Max() : null,
            MedianContextPercent = PercentileOf(contextPercents, 50),
            MaxContextPercent = contextPercents.Count > 0 ? Math.Round(contextPercents.Max(), 1) : null,
            // One window across every reported turn, or nothing: two windows mean two denominators,
            // and a single number over them would describe neither.
            ContextWindow = windows.Count == 1 ? windows.Single() : null,
            ThinkingTurns = thinking,
            TurnsWithoutTool = toolless,
            ToolCalls = toolCalls.Sum(kv => kv.Value.Calls),
            Tools = toolStats,
            PromptVersions = byPrompt
                .Select(kv => new PromptVersionStat
                {
                    Hash = kv.Key == NoHash ? null : kv.Key,
                    Turns = kv.Value.Turns,
                    OkTurns = kv.Value.Ok,
                    MedianMs = Percentile(kv.Value.Durations, 50),
                    NegativeTurns = kv.Value.Negative,
                    RatedTurns = kv.Value.Rated,
                })
                .OrderByDescending(p => p.Turns)
                .ThenBy(p => p.Hash, StringComparer.Ordinal)
                .ToList(),
            Activity = byDay.Select(kv => new DailyTurnCount { Date = kv.Key, Turns = kv.Value }).ToList(),
            RatedTurns = ratedTurns,
            PositiveTurns = positiveTurns,
            NegativeTurns = negativeTurns,
            // No votes means no satisfaction rate — not a rate of zero, which would assert that every
            // answer failed. Same rule the durations above follow.
            SatisfactionPercent = ratedTurns > 0
                ? Math.Round(positiveTurns * 100.0 / ratedTurns, 1)
                : null,
            FeedbackNotes = notes.OrderByDescending(n => n.At).ToList(),
        };
    }

    /// <summary>Nearest-rank percentile over a list of measurements; null when nothing was measured.</summary>
    private static long? Percentile(List<long> values, int percentile)
    {
        if (values.Count == 0) return null;
        var sorted = values.OrderBy(v => v).ToList();
        var rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }

    private static double? PercentileOf(List<double> values, int percentile)
    {
        if (values.Count == 0) return null;
        var sorted = values.OrderBy(v => v).ToList();
        var rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Count);
        return Math.Round(sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)], 1);
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

    public long AppendTurn(ConversationTurnRecord turn)
        => Insert(turn.ConversationId, KindTurn, turn.CompletedAt, JsonSerializer.Serialize(turn, Json));

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

    public bool SetTurnFeedback(string conversationId, long turnId, TurnFeedbackRating? rating, string? note)
    {
        // Entry ids are sequential across the WHOLE log, so an id alone says nothing about who owns it.
        // Confirm this id is a turn of THIS conversation before recording anything against it: without
        // that, a caller correctly scoped to its own conversation could still rate a stranger's turn by
        // naming a neighbouring id.
        using (var connection = Open())
        using (var check = connection.CreateCommand())
        {
            check.CommandText =
                "SELECT 1 FROM conversation_entries WHERE id = $id AND conversation_id = $cid AND kind = $turn;";
            check.Parameters.AddWithValue("$id", turnId);
            check.Parameters.AddWithValue("$cid", conversationId);
            check.Parameters.AddWithValue("$turn", KindTurn);
            if (check.ExecuteScalar() is null)
                return false;
        }

        // A cleared rating is stored as a record with a null rating rather than by removing the earlier
        // one — latest-wins resolution then reads it as "no verdict", and the log stays append-only.
        var payload = JsonSerializer.Serialize(
            new FeedbackPayload(turnId, rating, string.IsNullOrWhiteSpace(note) ? null : note.Trim()), Json);
        Insert(conversationId, KindFeedback, DateTimeOffset.UtcNow, payload);
        return true;
    }

    /// <summary>
    /// The stored shape of a <see cref="KindFeedback"/> entry. A null <see cref="Rating"/> is a cleared
    /// verdict, which is why the property is nullable rather than the record being absent.
    /// </summary>
    private sealed record FeedbackPayload(long TurnId, TurnFeedbackRating? Rating, string? Note);

    /// <summary>
    /// The stored shape of a <see cref="KindPreference"/> entry: a DELTA, so a null field is "this
    /// append said nothing about that switch" rather than "unset it". Resolution takes the newest
    /// non-null value per field, which is what lets the two switches be set independently without
    /// either one having to read the other first.
    /// </summary>
    private sealed record PreferencePayload(bool? Think, bool? Autorun);

    public ConversationPreferences GetPreferences(string conversationId)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        // Newest first, and the fold below takes the first non-null it sees for each field — so the scan
        // stops mattering as soon as both are answered. Preference entries are a handful per
        // conversation (one per time somebody flipped a switch), so this is bounded by hand-typing.
        cmd.CommandText =
            """
            SELECT payload FROM conversation_entries
            WHERE conversation_id = $cid AND kind = $preference
            ORDER BY id DESC;
            """;
        cmd.Parameters.AddWithValue("$cid", conversationId);
        cmd.Parameters.AddWithValue("$preference", KindPreference);

        bool? think = null, autorun = null;
        using var reader = cmd.ExecuteReader();
        while (reader.Read() && (think is null || autorun is null))
        {
            var recorded = JsonSerializer.Deserialize<PreferencePayload>(reader.GetString(0), Json);
            if (recorded is null)
                continue;
            think ??= recorded.Think;
            autorun ??= recorded.Autorun;
        }

        return new ConversationPreferences(think, autorun);
    }

    public void SetPreferences(string conversationId, ConversationPreferences delta)
    {
        // An append saying nothing is still an append — and it would sit in the log claiming a switch was
        // touched when none was. Nothing to record is not an error, it is simply no entry.
        if (delta.Think is null && delta.Autorun is null)
            return;

        Insert(conversationId, KindPreference, DateTimeOffset.UtcNow,
            JsonSerializer.Serialize(new PreferencePayload(delta.Think, delta.Autorun), Json));
    }

    public bool CreateConversation(string conversationId)
    {
        lock (_writeGate)
        {
            using (var connection = Open())
            using (var exists = connection.CreateCommand())
            {
                // Idempotent by existence, not by kind: a conversation that already holds turns is a
                // conversation, and re-creating it would append a second birth to a log that already
                // says when it started. Under the write gate so two callers cannot both find it absent.
                exists.CommandText =
                    "SELECT 1 FROM conversation_entries WHERE conversation_id = $cid LIMIT 1;";
                exists.Parameters.AddWithValue("$cid", conversationId);
                if (exists.ExecuteScalar() is not null)
                    return false;
            }

            using var connection2 = Open();
            using var cmd = connection2.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO conversation_entries (conversation_id, kind, created_at, payload)
                VALUES ($cid, $kind, $createdAt, '{}');
                """;
            cmd.Parameters.AddWithValue("$cid", conversationId);
            cmd.Parameters.AddWithValue("$kind", KindCreated);
            cmd.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
            return true;
        }
    }

    private long Insert(string conversationId, string kind, DateTimeOffset createdAt, string payload)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO conversation_entries (conversation_id, kind, created_at, payload)
                VALUES ($cid, $kind, $createdAt, $payload);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$cid", conversationId);
            cmd.Parameters.AddWithValue("$kind", kind);
            cmd.Parameters.AddWithValue("$createdAt", createdAt.ToString("O"));
            cmd.Parameters.AddWithValue("$payload", payload);
            // Read back under the same lock as the insert — last_insert_rowid() is per-connection, and
            // the connection is this call's own, so it can only ever name the row just written.
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    }

    private List<ConversationEntry> LoadEntries(string conversationId)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT id, kind, created_at, payload FROM conversation_entries WHERE conversation_id = $cid ORDER BY id ASC;";
        cmd.Parameters.AddWithValue("$cid", conversationId);

        var entries = new List<ConversationEntry>();
        // Verdicts are appended after the turn they judge, so they are collected on the way past and
        // attached below. Latest-wins falls out of the id ordering: a later verdict simply overwrites
        // the earlier one in the map, and a cleared one leaves a null behind.
        var feedback = new Dictionary<long, TurnFeedback?>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var kind = reader.GetString(1);
            var createdAt = DateTimeOffset.Parse(reader.GetString(2));
            var payload = reader.GetString(3);

            if (kind == KindDeleted)
            {
                // A soft-delete tombstone is bookkeeping, not content — never surfaced in the transcript
                // (and its payload is empty, so it must not reach the turn deserializer below).
                continue;
            }
            if (kind == KindFeedback)
            {
                var recorded = JsonSerializer.Deserialize<FeedbackPayload>(payload, Json);
                if (recorded is not null)
                {
                    feedback[recorded.TurnId] = recorded.Rating is null
                        ? null
                        : new TurnFeedback(recorded.Rating.Value, recorded.Note, createdAt);
                }
                continue;
            }
            if (kind == KindPreference || kind == KindCreated)
            {
                // Neither is a thing that was said. They carry their own payload shapes, so they must
                // not reach the turn deserializer in the else-branch below, which would read one as a
                // turn with every field defaulted and put an empty bubble in the transcript.
                continue;
            }
            if (kind == KindCheckpoint)
            {
                entries.Add(ConversationEntry.ForCheckpoint(payload, createdAt) with { Id = id });
            }
            else
            {
                var turn = JsonSerializer.Deserialize<ConversationTurnRecord>(payload, Json);
                if (turn is not null)
                    entries.Add(ConversationEntry.ForTurn(turn) with { Id = id });
            }
        }

        if (feedback.Count == 0)
            return entries;

        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Kind == ConversationEntryKind.Turn
                && feedback.TryGetValue(entries[i].Id, out var verdict)
                && verdict is not null)
            {
                entries[i] = entries[i] with { Feedback = verdict };
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
