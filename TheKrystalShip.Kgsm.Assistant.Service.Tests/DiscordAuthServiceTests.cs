using FluentAssertions;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Service.Configuration;
using TheKrystalShip.Kgsm.Assistant.Service.Security;
using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Discord;
using TheKrystalShip.KGSM.Auth.Sessions;
using TheKrystalShip.Llm.Conversation;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

/// <summary>
/// The login and authority lane, driven against a substituted Discord seam and a REAL SQLite session
/// registry in a temp file — so rotation and revocation are exercised as they actually run, not
/// against a stand-in that cannot disagree with the real thing.
/// </summary>
public class DiscordAuthServiceTests : IDisposable
{
    private const string OperatorRole = "role-1";
    private const string AdminRole = "role-admin";

    private static readonly DiscordIdentity Alice =
        new("u1", "alice", "Alice", null, ["identify"]);

    private readonly string _dbDir = Path.Combine(
        Path.GetTempPath(), "kgsm-auth-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dbDir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private DiscordAuthService Build(
        IDiscordDirectory directory,
        out ISessionRegistry registry,
        out ISessionTokenService tokens,
        bool actionsEnabled = true,
        string operatorRoleId = OperatorRole,
        string adminRoleId = AdminRole,
        int roleCacheTtlSeconds = 60)
    {
        var authOptions = Options.Create(new AuthOptions
        {
            SigningKey = "a-stable-test-secret",
            HostId = "test-host",
            AccessTtlSeconds = 900,
            SessionTtlSeconds = 3600,
            RoleCacheTtlSeconds = roleCacheTtlSeconds,
        });

        Directory.CreateDirectory(_dbDir);
        registry = new SqliteSessionRegistry(Options.Create(new ConversationOptions
        {
            DatabasePath = Path.Combine(_dbDir, "conversations.db"),
        }));

        tokens = new SessionTokenService(
            authOptions.Value.ToSessionTokenOptions(), NullLogger<SessionTokenService>.Instance);

        var assistantOptions = Options.Create(new AssistantServiceOptions
        {
            ActionsEnabled = actionsEnabled,
        });

        // Through the shared options so the tests exercise the same string→map parse the host does.
        var roleMap = new KgsmAuthOptions
        {
            RoleAdminIds = adminRoleId,
            RoleOperatorIds = operatorRoleId,
        }.ToRoleMap();

        return new DiscordAuthService(
            directory,
            tokens,
            registry,
            new SessionValidator(registry, new MemoryCache(new MemoryCacheOptions()), TimeSpan.FromSeconds(5)),
            new DiscordTierCache(TimeSpan.FromSeconds(roleCacheTtlSeconds)),
            roleMap,
            authOptions,
            assistantOptions,
            NullLogger<DiscordAuthService>.Instance);
    }

    private DiscordAuthService BuildWithBlankHostId(
        IDiscordDirectory directory, out ISessionRegistry registry, out ISessionTokenService tokens)
    {
        var authOptions = Options.Create(new AuthOptions
        {
            SigningKey = "a-stable-test-secret",
            HostId = string.Empty,   // the shipped default: a settings file cannot name its host
            AccessTtlSeconds = 900,
            SessionTtlSeconds = 3600,
            RoleCacheTtlSeconds = 60,
        });

        Directory.CreateDirectory(_dbDir);
        registry = new SqliteSessionRegistry(Options.Create(new ConversationOptions
        {
            DatabasePath = Path.Combine(_dbDir, "conversations.db"),
        }));
        tokens = new SessionTokenService(
            authOptions.Value.ToSessionTokenOptions(), NullLogger<SessionTokenService>.Instance);

        var assistantOptions = Options.Create(new AssistantServiceOptions
        {
            ActionsEnabled = true,
        });

        return new DiscordAuthService(
            directory, tokens, registry,
            new SessionValidator(registry, new MemoryCache(new MemoryCacheOptions()), TimeSpan.FromSeconds(5)),
            new DiscordTierCache(TimeSpan.FromSeconds(60)),
            new KgsmAuthOptions { RoleAdminIds = AdminRole, RoleOperatorIds = OperatorRole }.ToRoleMap(),
            authOptions, assistantOptions, NullLogger<DiscordAuthService>.Instance);
    }

    private static AuthPrincipal Principal(string userId = "u1", string sessionId = "sid_x") =>
        new(userId, "Alice", sessionId);

    [Fact]
    public void TheAuthorizeUrlCarriesThisHandshakesChallengeAndNotItsVerifier()
    {
        var directory = Substitute.For<IDiscordDirectory>();
        DiscordAuthService service = Build(directory, out _, out _);
        var handshake = OAuthHandshake.Create();

        service.BuildAuthorizeUrl(handshake, prompt: null);

        // The verifier is the secret half — it stays in the browser's cookie and is presented only at
        // the token exchange. Only the derived challenge may travel through a URL.
        directory.Received(1).BuildAuthorizeUrl(handshake.State, handshake.CodeChallenge, "none");
    }

    [Fact]
    public async Task ALoginMintsAPairAndRecordsARevocableSession()
    {
        var directory = Substitute.For<IDiscordDirectory>();
        DiscordAuthService service = Build(directory, out ISessionRegistry registry, out _);

        AuthSessionResult session = await service.CreateSessionAsync(
            new ResolvedPrincipal(Alice, KgsmTier.Operator), "a-browser", CancellationToken.None);

        session.Tier.Should().Be(KgsmTier.Operator);
        session.AccessToken.Should().NotBeNullOrEmpty();
        session.RefreshToken.Should().NotBeNullOrEmpty();
        // The access bearer must die long before the session does, or its short life bounds nothing.
        session.AccessExpires.Should().BeBefore(session.RefreshExpires);

        // A row exists and is alive — without it the bearer authenticates nobody, because the filter
        // refuses a sid the registry does not know.
        string sessionId = await SessionIdOf(service, session.RefreshToken);
        (await registry.IsAliveAsync(sessionId)).Should().BeTrue();
    }

    [Fact]
    public async Task TheSessionRowIsScopedToTheSameHostTheTokensAreMintedUnder()
    {
        // One host identity, one spelling. The audience comes from the resolved host (blank config
        // means this machine's name); a row written from the raw setting would say "" instead, and
        // the two would disagree about which host the session belongs to.
        var directory = Substitute.For<IDiscordDirectory>();
        DiscordAuthService service = BuildWithBlankHostId(directory, out ISessionRegistry registry, out ISessionTokenService tokens);

        AuthSessionResult session = await service.CreateSessionAsync(
            new ResolvedPrincipal(Alice, KgsmTier.Operator), null, CancellationToken.None);

        // A token minted for this machine validates; the row must claim the same host.
        RefreshClaims? claims = await tokens.ReadRefreshAsync(session.RefreshToken);
        claims.Should().NotBeNull();

        var registryRow = (SqliteSessionRegistry)registry;
        registryRow.HostOf(claims!.SessionId).Should().Be(Environment.MachineName);
    }

    [Fact]
    public async Task LoginSeedsTheTierSoTheFirstRequestDoesNotReAskDiscord()
    {
        var directory = Substitute.For<IDiscordDirectory>();
        DiscordAuthService service = Build(directory, out _, out _);

        await service.CreateSessionAsync(
            new ResolvedPrincipal(Alice, KgsmTier.Operator), null, CancellationToken.None);

        (await service.CanPerformActionsAsync(Principal())).Should().BeTrue();

        await directory.DidNotReceive().GetGuildRolesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARefreshRotatesAndTheOldTokenStopsWorking()
    {
        // Reuse detection: a refresh token is single-use. Presenting a rotated-away one means either a
        // stale client or a stolen token, and there is no way to tell which — so neither is renewed.
        var directory = Substitute.For<IDiscordDirectory>();
        DiscordAuthService service = Build(directory, out _, out _);

        AuthSessionResult first = await service.CreateSessionAsync(
            new ResolvedPrincipal(Alice, KgsmTier.Operator), null, CancellationToken.None);

        AuthSessionResult? second = await service.RefreshAsync(first.RefreshToken, CancellationToken.None);
        second.Should().NotBeNull();
        second!.RefreshToken.Should().NotBe(first.RefreshToken);

        (await service.RefreshAsync(first.RefreshToken, CancellationToken.None)).Should().BeNull();
        // The rotation the legitimate client got is still good — a replay must not kill the real session.
        (await service.RefreshAsync(second.RefreshToken, CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task AnAccessTokenCannotBeSpentAsARefreshToken()
    {
        var directory = Substitute.For<IDiscordDirectory>();
        DiscordAuthService service = Build(directory, out _, out _);

        AuthSessionResult session = await service.CreateSessionAsync(
            new ResolvedPrincipal(Alice, KgsmTier.Operator), null, CancellationToken.None);

        (await service.RefreshAsync(session.AccessToken, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task LogoutKillsTheSessionAndItsRefreshToken()
    {
        var directory = Substitute.For<IDiscordDirectory>();
        DiscordAuthService service = Build(directory, out ISessionRegistry registry, out _);

        AuthSessionResult session = await service.CreateSessionAsync(
            new ResolvedPrincipal(Alice, KgsmTier.Operator), null, CancellationToken.None);
        string sessionId = await SessionIdOf(service, session.RefreshToken);

        await service.LogoutAsync(Principal(sessionId: sessionId));

        (await registry.IsAliveAsync(sessionId)).Should().BeFalse();
        (await service.RefreshAsync(session.RefreshToken, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task AuthorityIsCachedWithinTheTtl()
    {
        var directory = Substitute.For<IDiscordDirectory>();
        DiscordAuthService service = Build(directory, out _, out _);
        directory.GetGuildRolesAsync("u1", Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<string>?>([OperatorRole]);

        (await service.CanPerformActionsAsync(Principal())).Should().BeTrue();
        (await service.CanPerformActionsAsync(Principal())).Should().BeTrue();

        await directory.Received(1).GetGuildRolesAsync("u1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotBeingAGuildMemberDenies()
    {
        var directory = Substitute.For<IDiscordDirectory>();
        DiscordAuthService service = Build(directory, out _, out _);
        // null is "not a member", which is a different answer from a member holding no roles.
        directory.GetGuildRolesAsync("u1", Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<string>?)null);

        (await service.ResolveTierAsync(Principal())).Should().Be(KgsmTier.None);
        (await service.CanPerformActionsAsync(Principal())).Should().BeFalse();
    }

    [Fact]
    public async Task AMemberHoldingNoRolesFloorsAtViewer()
    {
        var directory = Substitute.For<IDiscordDirectory>();
        DiscordAuthService service = Build(directory, out _, out _);
        directory.GetGuildRolesAsync("u1", Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<string>?>([]);

        (await service.ResolveTierAsync(Principal())).Should().Be(KgsmTier.Viewer);
        (await service.CanPerformActionsAsync(Principal())).Should().BeFalse();
    }

    [Fact]
    public async Task AnUnreachableDiscordDeniesTheCheckAndIsNotCachedAsAnAnswer()
    {
        // "We could not ask" is not "the answer is no". Caching the failure would turn a brief Discord
        // outage into a full-TTL lockout for someone who really is an operator.
        var directory = Substitute.For<IDiscordDirectory>();
        DiscordAuthService service = Build(directory, out _, out _);

        directory.GetGuildRolesAsync("u1", Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<string>?>(_ => throw new DiscordAuthException("Discord unreachable."));

        (await service.CanPerformActionsAsync(Principal())).Should().BeFalse();

        directory.GetGuildRolesAsync("u1", Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<string>?>([OperatorRole]);

        (await service.CanPerformActionsAsync(Principal())).Should().BeTrue();
    }

    [Fact]
    public async Task ReviewNeedsAdminNotOperator()
    {
        var directory = Substitute.For<IDiscordDirectory>();
        DiscordAuthService service = Build(directory, out _, out _);
        directory.GetGuildRolesAsync("u1", Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<string>?>([OperatorRole]);

        (await service.CanPerformActionsAsync(Principal())).Should().BeTrue();
        (await service.IsAdminAsync(Principal())).Should().BeFalse();
    }

    [Fact]
    public async Task AdminCanDoEverythingOperatorCan()
    {
        var directory = Substitute.For<IDiscordDirectory>();
        DiscordAuthService service = Build(directory, out _, out _);
        directory.GetGuildRolesAsync("u1", Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<string>?>([AdminRole]);

        (await service.IsAdminAsync(Principal())).Should().BeTrue();
        (await service.CanPerformActionsAsync(Principal())).Should().BeTrue();
    }

    [Fact]
    public async Task NoConfiguredAdminRoleMeansNobodyReviews()
    {
        var directory = Substitute.For<IDiscordDirectory>();
        DiscordAuthService service = Build(directory, out _, out _, adminRoleId: "");

        (await service.IsAdminAsync(Principal())).Should().BeFalse();
        await directory.DidNotReceive().GetGuildRolesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheMasterSwitchDeniesWithoutCallingDiscord()
    {
        var directory = Substitute.For<IDiscordDirectory>();
        DiscordAuthService service = Build(directory, out _, out _, actionsEnabled: false);

        (await service.CanPerformActionsAsync(Principal())).Should().BeFalse();
        await directory.DidNotReceive().GetGuildRolesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoConfiguredOperatorRoleDenies()
    {
        var directory = Substitute.For<IDiscordDirectory>();
        DiscordAuthService service = Build(directory, out _, out _, operatorRoleId: "", adminRoleId: "");

        (await service.CanPerformActionsAsync(Principal())).Should().BeFalse();
        await directory.DidNotReceive().GetGuildRolesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>The session id a minted refresh token carries, read back the way the service reads it.</summary>
    private static async Task<string> SessionIdOf(DiscordAuthService service, string refreshToken)
    {
        // Rotating and reading back would consume the token, so go through the token service directly.
        var tokens = new SessionTokenService(
            new AuthOptions { SigningKey = "a-stable-test-secret", HostId = "test-host", SessionTtlSeconds = 3600 }
                .ToSessionTokenOptions(),
            NullLogger<SessionTokenService>.Instance);
        RefreshClaims? claims = await tokens.ReadRefreshAsync(refreshToken);
        claims.Should().NotBeNull();
        return claims!.SessionId;
    }
}
