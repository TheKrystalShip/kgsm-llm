using Microsoft.Data.Sqlite;

using TheKrystalShip.Kgsm.Assistant.Service.Security;
using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Users;

namespace TheKrystalShip.Kgsm.Assistant.Service.Conversation;

/// <summary>
/// Moves what somebody owns from the credential that proved them onto the account they are.
/// </summary>
/// <remarks>
/// <para>
/// A conversation, a memory, a staged action and a notification device were all keyed by the
/// provider's id for whoever signed in. One person therefore had a separate set per door — a Discord
/// sign-in and a password sign-in produced two histories that never met — and the split was silent,
/// because each door found exactly what it had written. Keying on the account instead makes one
/// person one person, whichever door they come through.
/// </para>
/// <para>
/// <b>Once, and only where an account is found.</b> An owner segment the account store cannot resolve
/// is left exactly where it is: it belongs to somebody this host has no account for — a probe, a
/// test, a person who never signed in here — and inventing an account for them would be worse than
/// leaving a namespace nobody reaches. Rooms are keyed by the place they happen in and have no owner
/// to move.
/// </para>
/// <para>
/// <b>Two histories for one person merge, in time order.</b> That is the intent rather than a
/// side effect: they were always one person's, and the rows carry their own timestamps, so reading
/// the result is reading what they actually said in the order they said it.
/// </para>
/// </remarks>
internal static class OwnerRekeyMigration
{
    /// <summary>The name recorded once this has run, so it never runs twice.</summary>
    private const string Name = "conversation-owner-is-the-account";

    /// <summary>What a copy of the database is called when one is taken.</summary>
    private const string BackupSuffix = ".pre-owner-rekey";

    /// <summary>
    /// Run it, if it has not run. Nothing is written when there is nothing to move.
    /// </summary>
    /// <remarks>
    /// Synchronous and before the host serves anything: rewriting the key every read and write uses,
    /// underneath live requests, would have some of them find the old key and some the new.
    /// </remarks>
    public static async Task RunAsync(
        string databasePath, UserDirectory users, ILogger logger, CancellationToken ct = default)
    {
        if (!File.Exists(databasePath))
            return;

        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            DefaultTimeout = 30,
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);

        await EnsureLedgerAsync(connection, ct);
        if (await AlreadyRunAsync(connection, ct))
            return;

        // A host that has never held a conversation has nothing keyed by anything. The file exists
        // because some other store on it opened first, which is ordinary — and this is deliberately
        // not recorded as done, because a corpus that appears later is one this should still look at.
        if (!await HasTableAsync(connection, "conversation_entries", ct))
            return;

        if (!users.Available)
        {
            // Without the accounts there is nothing to resolve owners against, and running with an
            // empty answer would conclude that nobody has an account and move nothing — recording
            // that as done. Left for the next start, which is when the store is readable again.
            logger.LogWarning(
                "the conversation owner re-key is waiting: the account store is unavailable ({Reason})",
                users.UnavailableReason);
            return;
        }

        IReadOnlyList<string> owners = await OwnersAsync(connection, ct);
        var moves = new Dictionary<string, string>(StringComparer.Ordinal);
        var unresolved = new List<string>();

        foreach (string owner in owners)
        {
            string? account = await AccountForAsync(users.Store, owner, ct);
            if (account is null)
                unresolved.Add(owner);
            else if (!string.Equals(account, owner, StringComparison.Ordinal))
                moves[owner] = account;
        }

        // Said before anything is written, because this is the one chance to see what it is about to
        // do to a corpus that cannot be reconstructed.
        foreach ((string from, string to) in moves)
            logger.LogInformation("conversation owner re-key: {From} becomes {To}", from, to);

        if (unresolved.Count > 0)
        {
            logger.LogInformation(
                "conversation owner re-key: {Count} owner(s) have no account here and stay as they are: {Owners}",
                unresolved.Count, string.Join(", ", unresolved));
        }

        if (moves.Count == 0)
        {
            await RecordAsync(connection, ct);
            logger.LogInformation("conversation owner re-key: nothing to move");
            return;
        }

        Backup(connection, databasePath, logger);
        await ApplyAsync(connection, moves, ct);
        await RecordAsync(connection, ct);

        logger.LogInformation(
            "conversation owner re-key: moved {Count} owner(s) onto their accounts", moves.Count);
    }

    /// <summary>
    /// A copy of the whole database as it stands, taken through SQLite rather than by copying the
    /// file: a file copy taken while a write-ahead log is live captures a database mid-sentence.
    /// </summary>
    /// <remarks>
    /// A failure to take one does not stop the migration. The re-key is a prefix rewrite of rows that
    /// are still all there, and refusing to run because a backup could not be written would leave the
    /// corpus split — which is the thing being fixed.
    /// </remarks>
    private static void Backup(SqliteConnection connection, string databasePath, ILogger logger)
    {
        string path = databasePath + BackupSuffix;
        try
        {
            if (File.Exists(path))
            {
                logger.LogInformation("conversation owner re-key: a copy already exists at {Path}", path);
                return;
            }

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"VACUUM INTO '{path.Replace("'", "''")}';";
            command.ExecuteNonQuery();
            logger.LogInformation("conversation owner re-key: copied the database to {Path} first", path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "conversation owner re-key: could not copy the database to {Path}", path);
        }
    }

    /// <summary>Every owner segment currently in use, across everything that carries one.</summary>
    private static async Task<IReadOnlyList<string>> OwnersAsync(SqliteConnection connection, CancellationToken ct)
    {
        // Only the web scope. A room is keyed by the place rather than by anybody, so it has no owner
        // segment to read and nothing here would be true of it.
        //
        // Built from the tables that are actually there. They are created by different stores, each
        // when it is first used, so a host can legitimately have the conversations and not yet the
        // notifications — and naming a table that does not exist fails the whole read rather than the
        // part of it that was never applicable.
        var sources = new List<string>();
        if (await HasTableAsync(connection, "conversation_entries", ct))
        {
            sources.Add(
                """
                SELECT CASE WHEN instr(substr(conversation_id, 5), ':') > 0
                            THEN substr(substr(conversation_id, 5), 1, instr(substr(conversation_id, 5), ':') - 1)
                            ELSE substr(conversation_id, 5) END AS owner
                  FROM conversation_entries WHERE conversation_id LIKE 'web:%'
                """);
        }

        if (await HasTableAsync(connection, "memory_entries", ct))
        {
            sources.Add(
                """
                SELECT CASE WHEN instr(substr(owner_key, 5), ':') > 0
                            THEN substr(substr(owner_key, 5), 1, instr(substr(owner_key, 5), ':') - 1)
                            ELSE substr(owner_key, 5) END
                  FROM memory_entries WHERE owner_key LIKE 'web:%'
                """);
        }

        if (await HasTableAsync(connection, "push_subscriptions", ct))
            sources.Add("SELECT user_id FROM push_subscriptions");

        if (await HasTableAsync(connection, "pending_confirmations", ct))
            sources.Add("SELECT staged_by FROM pending_confirmations");

        if (sources.Count == 0)
            return [];

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT DISTINCT owner FROM (" + string.Join("\n UNION \n", sources)
            + ") WHERE owner IS NOT NULL AND owner <> '';";

        var owners = new List<string>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            owners.Add(reader.GetString(0));

        return owners;
    }

    /// <summary>
    /// The account an owner segment belongs to, or <see langword="null"/> when none does.
    /// </summary>
    /// <remarks>
    /// An owner that is already an account id resolves to itself, which is how a corpus written after
    /// a password sign-in is recognised as needing nothing. Otherwise it is a provider's subject, and
    /// the credential that carries it names the account — tried against every provider this store
    /// knows rather than against a guessed one, because which door somebody used is not recorded in
    /// the key.
    /// </remarks>
    private static async Task<string?> AccountForAsync(IUserStore store, string owner, CancellationToken ct)
    {
        if (await store.FindByIdAsync(owner, ct) is { } itself)
            return itself.UserId;

        foreach (string provider in new[] { KgsmActorProvider.Discord, KgsmActorProvider.Local })
        {
            if (await store.FindByCredentialAsync(KgsmActor.Format(provider, owner), ct) is { } found)
                return found.UserId;
        }

        return null;
    }

    /// <summary>
    /// Rewrite every owner in one transaction, so a crash leaves the corpus as it was rather than
    /// half moved.
    /// </summary>
    private static async Task ApplyAsync(
        SqliteConnection connection, IReadOnlyDictionary<string, string> moves, CancellationToken ct)
    {
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        bool conversations = await HasTableAsync(connection, "conversation_entries", ct);
        bool memories = await HasTableAsync(connection, "memory_entries", ct);
        bool confirmations = await HasTableAsync(connection, "pending_confirmations", ct);
        bool subscriptions = await HasTableAsync(connection, "push_subscriptions", ct);

        foreach ((string from, string to) in moves)
        {
            if (conversations)
                await ExecuteAsync(connection, transaction, ct,
                """
                UPDATE conversation_entries
                   SET conversation_id = 'web:' || $to || substr(conversation_id, 5 + length($from))
                 WHERE conversation_id = 'web:' || $from
                    OR conversation_id LIKE 'web:' || $from || ':%';
                """, from, to);

            if (memories)
                await ExecuteAsync(connection, transaction, ct,
                """
                UPDATE memory_entries
                   SET owner_key = 'web:' || $to || substr(owner_key, 5 + length($from))
                 WHERE owner_key = 'web:' || $from
                    OR owner_key LIKE 'web:' || $from || ':%';
                """, from, to);

            if (confirmations)
                await ExecuteAsync(connection, transaction, ct,
                """
                UPDATE pending_confirmations
                   SET staged_by = $to
                 WHERE staged_by = $from;
                """, from, to);

            if (confirmations)
                await ExecuteAsync(connection, transaction, ct,
                """
                UPDATE pending_confirmations
                   SET conversation_id = 'web:' || $to || substr(conversation_id, 5 + length($from))
                 WHERE conversation_id = 'web:' || $from
                    OR conversation_id LIKE 'web:' || $from || ':%';
                """, from, to);

            // The devices somebody takes notifications on are theirs, not their credential's, so they
            // follow the person. Left behind they would belong to a key nothing looks up again, and
            // the person would silently stop being notified.
            if (subscriptions)
                await ExecuteAsync(connection, transaction, ct,
                    "UPDATE push_subscriptions SET user_id = $to WHERE user_id = $from;", from, to);
        }

        await transaction.CommitAsync(ct);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct,
        string sql, string from, string to)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Whether a table this migration reads is present in this database yet.</summary>
    private static async Task<bool> HasTableAsync(SqliteConnection connection, string table, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", table);
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task EnsureLedgerAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS migrations (
                name        TEXT PRIMARY KEY,
                applied_utc TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> AlreadyRunAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM migrations WHERE name = $name;";
        command.Parameters.AddWithValue("$name", Name);
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task RecordAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "INSERT OR REPLACE INTO migrations (name, applied_utc) VALUES ($name, $now);";
        command.Parameters.AddWithValue("$name", Name);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }
}
