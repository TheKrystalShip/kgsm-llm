using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Llm.Conversation;

/// <summary>
/// Durable memory in SQLite: an append-only log of writes and tombstones (<c>memory_entries</c>),
/// resolved latest-wins per <c>(owner, key)</c>. Rewriting a memory appends; forgetting one appends;
/// nothing is ever updated or deleted in place, so the log keeps what a memory used to say.
/// <para>
/// Shares the conversation store's database file (<see cref="ConversationOptions.DatabasePath"/>) —
/// one file holds this assistant's whole durable state, as it already does for the pending
/// confirmations and the session registry. Each call opens a pooled connection; writes are serialised
/// through a process lock (SQLite is single-writer) while WAL lets reads run concurrently.
/// </para>
/// </summary>
public sealed class SqliteMemoryStore : IMemoryStore
{
    /// <summary>A memory as it stands: the newest write under a key is what it says.</summary>
    private const string KindWrite = "write";

    /// <summary>
    /// A forgotten memory. Append-only and latest-wins exactly like the conversation store's
    /// soft-delete tombstone: the writes stay in the log, and a later write of the same key is newer
    /// than the tombstone, so re-remembering something is an append rather than an undelete.
    /// </summary>
    private const string KindForgotten = "forgotten";

    // Web defaults + camelCase enums, matching SqliteConversationStore on the same file. Nothing here
    // rides the wire today, but two stores writing JSON into one database in two different casings is
    // the divergence that already had to be corrected once in the conversation store.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _connectionString;
    private readonly MemoryOptions _options;

    // SQLite is single-writer; serialise writes so concurrent turns never hit "database is locked".
    private readonly object _writeGate = new();

    public SqliteMemoryStore(IOptions<ConversationOptions> conversation, IOptions<MemoryOptions> memory)
    {
        _options = memory.Value;

        // The same default-path rule as SqliteConversationStore, because it is the same file: a
        // configured path wins, otherwise a file beside the host binary so the store always has a home.
        var databasePath = string.IsNullOrWhiteSpace(conversation.Value.DatabasePath)
            ? Path.Combine(AppContext.BaseDirectory, "conversations.db")
            : conversation.Value.DatabasePath;

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
        // WAL is very likely already set by whichever store opened this file first; setting it again is
        // idempotent and keeps this store standing alone (a host may compose it without the other).
        cmd.CommandText =
            """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS memory_entries (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                owner_key   TEXT    NOT NULL,
                memory_key  TEXT    NOT NULL,
                kind        TEXT    NOT NULL,
                created_at  TEXT    NOT NULL,
                payload     TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_memory_owner
                ON memory_entries (owner_key, memory_key, id);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// The row that stands for each of an owner's keys: the newest entry per key, whatever its kind.
    /// A tombstone winning is how a forgotten memory disappears without anything being removed, and a
    /// write winning over a tombstone is how re-remembering works — one ordering, both behaviours.
    /// Expects an <c>$owner</c> parameter.
    /// </summary>
    private const string StandingSql =
        """
        SELECT memory_key, kind, created_at, payload,
               ROW_NUMBER() OVER (PARTITION BY memory_key ORDER BY id DESC) AS rn
        FROM memory_entries
        WHERE owner_key = $owner
        """;

    /// <summary>The stored shape of a <see cref="KindWrite"/> entry. A tombstone carries an empty
    /// payload — its kind and position are all that matter.</summary>
    private sealed record MemoryPayload(string Summary, string Body, string? Origin);

    public IReadOnlyList<MemoryRecord> List(string ownerKey)
    {
        if (string.IsNullOrEmpty(ownerKey))
            return [];

        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            $"""
             SELECT memory_key, kind, created_at, payload FROM (
                 {StandingSql}
             ) WHERE rn = 1 AND kind = $write
             ORDER BY created_at DESC, memory_key;
             """;
        cmd.Parameters.AddWithValue("$owner", ownerKey);
        cmd.Parameters.AddWithValue("$write", KindWrite);

        var memories = new List<MemoryRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (Read(reader.GetString(0), reader.GetString(2), reader.GetString(3)) is { } memory)
                memories.Add(memory);
        }

        return memories;
    }

    public MemoryRecord? Get(string ownerKey, string key)
    {
        if (string.IsNullOrEmpty(ownerKey) || string.IsNullOrEmpty(key))
            return null;

        using var connection = Open();
        using var cmd = connection.CreateCommand();
        // Newest first and take one: the top row is what stands, and a tombstone on top means nothing
        // does. Reading only the newest is why forgetting never has to find the writes it hides.
        cmd.CommandText =
            """
            SELECT kind, created_at, payload FROM memory_entries
            WHERE owner_key = $owner AND memory_key = $key
            ORDER BY id DESC LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$owner", ownerKey);
        cmd.Parameters.AddWithValue("$key", key);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read() || reader.GetString(0) != KindWrite)
            return null;

        return Read(key, reader.GetString(1), reader.GetString(2));
    }

    public bool Write(string ownerKey, MemoryRecord memory)
    {
        if (string.IsNullOrEmpty(ownerKey) || string.IsNullOrEmpty(memory.Key))
            return false;

        lock (_writeGate)
        {
            // The cap counts distinct standing keys, and a rewrite adds none — so it is allowed at the
            // cap. Refusing it would leave a full owner unable to correct a memory that is wrong, which
            // is the one write you least want to block. Checked under the gate so two concurrent turns
            // cannot both find room for the last slot.
            if (Get(ownerKey, memory.Key) is null && Count(ownerKey) >= _options.MaxPerOwner)
                return false;

            Insert(ownerKey, memory.Key, KindWrite, memory.WrittenAt,
                JsonSerializer.Serialize(new MemoryPayload(memory.Summary, memory.Body, memory.Origin), Json));
            return true;
        }
    }

    public bool Forget(string ownerKey, string key)
    {
        if (string.IsNullOrEmpty(ownerKey) || string.IsNullOrEmpty(key))
            return false;

        lock (_writeGate)
        {
            // Nothing standing is reported rather than tombstoned. An append here would be a marker
            // hiding nothing, and the caller needs the false to say which keys do exist instead of
            // reporting a success that changed nothing.
            if (Get(ownerKey, key) is null)
                return false;

            Insert(ownerKey, key, KindForgotten, DateTimeOffset.UtcNow, string.Empty);
            return true;
        }
    }

    public int Count(string ownerKey)
    {
        if (string.IsNullOrEmpty(ownerKey))
            return 0;

        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            $"""
             SELECT COUNT(*) FROM (
                 {StandingSql}
             ) WHERE rn = 1 AND kind = $write;
             """;
        cmd.Parameters.AddWithValue("$owner", ownerKey);
        cmd.Parameters.AddWithValue("$write", KindWrite);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// Rebuilds a record from a stored write. A payload that will not deserialize answers null rather
    /// than throwing: one unreadable row must not take out the listing that carries every other
    /// memory the person has.
    /// </summary>
    private static MemoryRecord? Read(string key, string createdAt, string payload)
    {
        MemoryPayload? stored;
        try
        {
            stored = JsonSerializer.Deserialize<MemoryPayload>(payload, Json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (stored is null)
            return null;

        var writtenAt = DateTimeOffset.TryParse(createdAt, out var parsed) ? parsed : DateTimeOffset.MinValue;
        return new MemoryRecord(key, stored.Summary, stored.Body, writtenAt, stored.Origin);
    }

    private void Insert(string ownerKey, string key, string kind, DateTimeOffset createdAt, string payload)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO memory_entries (owner_key, memory_key, kind, created_at, payload)
            VALUES ($owner, $key, $kind, $createdAt, $payload);
            """;
        cmd.Parameters.AddWithValue("$owner", ownerKey);
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$createdAt", createdAt.ToString("O"));
        cmd.Parameters.AddWithValue("$payload", payload);
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
