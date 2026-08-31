using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using FluentAssertions;

using TheKrystalShip.Kgsm.Assistant.Service.Configuration;
using TheKrystalShip.Kgsm.Assistant.Service.Conversation;
using TheKrystalShip.Kgsm.Assistant.Service.Security;
using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Users;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

/// <summary>
/// Moving what somebody owns onto the account they are.
/// </summary>
/// <remarks>
/// Against real files, because what this does is rewrite a corpus that cannot be reconstructed — and
/// the cases that matter are the ones where it must NOT rewrite: an owner with no account, and a room,
/// which belongs to a place rather than a person.
/// </remarks>
public sealed class OwnerRekeyMigrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "kgsm-assistant-rekey-" + Guid.NewGuid().ToString("N"));

    private string ConversationsPath => Path.Combine(_directory, "conversations.db");
    private string UsersPath => Path.Combine(_directory, "users.db");

    public OwnerRekeyMigrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch { /* a temp dir outliving a run costs nothing */ }
    }

    private UserDirectory Users() => new(
        Options.Create(new AuthOptions { UsersDbPath = UsersPath }),
        NullLogger<UserDirectory>.Instance);

    /// <summary>An account proved by a Discord identity, as replication or a sign-in would leave it.</summary>
    private async Task<string> AccountAsync(string username, string discordId)
    {
        var store = new SqliteUserStore(new UserStoreOptions { Path = UsersPath });
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var user = new KgsmUser(
            UserIds.NewUserId(), username, username, KgsmTier.Operator, TierSource.Granted,
            UserStatus.Active, now, now);
        await store.CreateAsync(user);
        await store.AddCredentialAsync(new UserCredential(
            UserIds.NewCredentialId(), user.UserId, CredentialKind.Identity,
            KgsmActor.Format(KgsmActorProvider.Discord, discordId), null, username, now, null));
        return user.UserId;
    }

    private async Task SeedCorpusAsync(params (string Table, string Column, string Value)[] rows)
    {
        await using var connection = new SqliteConnection($"Data Source={ConversationsPath}");
        await connection.OpenAsync();
        await using (SqliteCommand create = connection.CreateCommand())
        {
            create.CommandText =
                """
                CREATE TABLE conversation_entries (id INTEGER PRIMARY KEY AUTOINCREMENT,
                    conversation_id TEXT NOT NULL, kind TEXT NOT NULL DEFAULT 'm',
                    created_at TEXT NOT NULL DEFAULT '', payload TEXT NOT NULL DEFAULT '');
                CREATE TABLE memory_entries (id INTEGER PRIMARY KEY AUTOINCREMENT, owner_key TEXT NOT NULL,
                    memory_key TEXT NOT NULL DEFAULT '', kind TEXT NOT NULL DEFAULT '',
                    created_at TEXT NOT NULL DEFAULT '', payload TEXT NOT NULL DEFAULT '');
                CREATE TABLE pending_confirmations (id TEXT PRIMARY KEY, kind INTEGER NOT NULL DEFAULT 0,
                    target TEXT NOT NULL DEFAULT '', staged_by TEXT NOT NULL DEFAULT '',
                    expires_at TEXT NOT NULL DEFAULT '', conversation_id TEXT);
                CREATE TABLE push_subscriptions (endpoint TEXT PRIMARY KEY, user_id TEXT NOT NULL,
                    p256dh TEXT NOT NULL DEFAULT '', auth TEXT NOT NULL DEFAULT '',
                    origin TEXT NOT NULL DEFAULT '', created_at TEXT NOT NULL DEFAULT '');
                """;
            await create.ExecuteNonQueryAsync();
        }

        foreach ((string table, string column, string value) in rows)
        {
            await using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText = table switch
            {
                "push_subscriptions" => $"INSERT INTO push_subscriptions (endpoint, {column}) VALUES ('e' || abs(random()), $v);",
                "pending_confirmations" => $"INSERT INTO pending_confirmations (id, {column}) VALUES ('c' || abs(random()), $v);",
                _ => $"INSERT INTO {table} ({column}) VALUES ($v);",
            };
            insert.Parameters.AddWithValue("$v", value);
            await insert.ExecuteNonQueryAsync();
        }
    }

    private async Task<List<string>> ReadAsync(string table, string column)
    {
        await using var connection = new SqliteConnection($"Data Source={ConversationsPath}");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT {column} FROM {table} ORDER BY {column};";

        var values = new List<string>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            values.Add(reader.GetString(0));
        return values;
    }

    private Task RunAsync() =>
        OwnerRekeyMigration.RunAsync(ConversationsPath, Users(), NullLogger.Instance);

    [Fact]
    public async Task What_a_person_owns_follows_the_account_rather_than_the_door()
    {
        string account = await AccountAsync("alice", "245717107596197888");
        await SeedCorpusAsync(
            ("conversation_entries", "conversation_id", "web:245717107596197888"),
            ("conversation_entries", "conversation_id", "web:245717107596197888:chat1"),
            ("memory_entries", "owner_key", "web:245717107596197888"),
            ("pending_confirmations", "staged_by", "245717107596197888"),
            ("push_subscriptions", "user_id", "245717107596197888"));

        await RunAsync();

        (await ReadAsync("conversation_entries", "conversation_id"))
            .Should().Equal($"web:{account}", $"web:{account}:chat1");
        (await ReadAsync("memory_entries", "owner_key")).Should().Equal($"web:{account}");
        (await ReadAsync("pending_confirmations", "staged_by")).Should().Equal(account);

        // The devices somebody takes notifications on are theirs, not their credential's. Left behind
        // they would belong to a key nothing looks up, and they would silently stop being notified.
        (await ReadAsync("push_subscriptions", "user_id")).Should().Equal(account);
    }

    [Fact]
    public async Task An_owner_with_no_account_is_left_exactly_where_it_is()
    {
        // A probe, a test, somebody who never signed in here. Inventing an account for them would be
        // worse than a namespace nobody reaches.
        await AccountAsync("alice", "245717107596197888");
        await SeedCorpusAsync(
            ("conversation_entries", "conversation_id", "web:some-probe"),
            ("conversation_entries", "conversation_id", "web:245717107596197888"));

        await RunAsync();

        (await ReadAsync("conversation_entries", "conversation_id"))
            .Should().Contain("web:some-probe");
    }

    [Fact]
    public async Task A_room_belongs_to_a_place_and_does_not_move()
    {
        await AccountAsync("alice", "245717107596197888");
        await SeedCorpusAsync(
            ("conversation_entries", "conversation_id", "room:245717107596197888-v1"));

        await RunAsync();

        (await ReadAsync("conversation_entries", "conversation_id"))
            .Should().Equal("room:245717107596197888-v1");
    }

    [Fact]
    public async Task Two_histories_for_one_person_become_one()
    {
        // The intent rather than a side effect: they were always one person's, and each row carries
        // its own timestamp, so what is read afterwards is what they said in the order they said it.
        string account = await AccountAsync("alice", "245717107596197888");
        await SeedCorpusAsync(
            ("conversation_entries", "conversation_id", "web:245717107596197888"),
            ("conversation_entries", "conversation_id", $"web:{account}"));

        await RunAsync();

        (await ReadAsync("conversation_entries", "conversation_id"))
            .Should().Equal($"web:{account}", $"web:{account}");
    }

    [Fact]
    public async Task It_runs_once_and_takes_a_copy_first()
    {
        string account = await AccountAsync("alice", "245717107596197888");
        await SeedCorpusAsync(("conversation_entries", "conversation_id", "web:245717107596197888"));

        await RunAsync();
        File.Exists(ConversationsPath + ".pre-owner-rekey").Should().BeTrue();

        // A second run must not re-read the corpus and decide anything about it again. The marker is
        // what makes that impossible rather than merely unlikely.
        await SeedRowAsync("web:245717107596197888");
        await RunAsync();

        (await ReadAsync("conversation_entries", "conversation_id"))
            .Should().Contain("web:245717107596197888").And.Contain($"web:{account}");
    }

    private async Task SeedRowAsync(string conversationId)
    {
        await using var connection = new SqliteConnection($"Data Source={ConversationsPath}");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO conversation_entries (conversation_id) VALUES ($v);";
        command.Parameters.AddWithValue("$v", conversationId);
        await command.ExecuteNonQueryAsync();
    }
}
