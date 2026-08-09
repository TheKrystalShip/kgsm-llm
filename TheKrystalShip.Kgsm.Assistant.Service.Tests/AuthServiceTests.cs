using System.Text.Json;

using FluentAssertions;

using Microsoft.AspNetCore.Http;

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
/// The login and authority lane, driven against a substituted sign-in seam and a REAL SQLite session
/// registry in a temp file — so rotation and revocation are exercised as they actually run, not
/// against a stand-in that cannot disagree with the real thing.
/// </summary>
public class AuthServiceTests : IDisposable
{
    private static readonly KgsmIdentity Alice =
        new(KgsmActorProvider.Discord, "u1", "alice", "Alice", null, ["identify"]);

    /// <summary>
    /// Stub the authority the way the account store answers it: one tier per identity. There is
    /// nothing else to model — an identity attached to no account holds none, and which chat server
    /// anyone is in has no bearing on any of it.
    /// </summary>
    private static void StubTier(ISignInService seam, KgsmTier tier) =>
        ((IAuthorityProvider)seam).ResolveTierAsync(Arg.Any<KgsmIdentity>(), Arg.Any<CancellationToken>())
            .Returns(_ => tier);

    /// <summary>Stub the authority as unreachable — an outage, never a verdict.</summary>
    private static void StubUnreachable(ISignInService seam) =>
        ((IAuthorityProvider)seam).ResolveTierAsync(Arg.Any<KgsmIdentity>(), Arg.Any<CancellationToken>())
            .Returns<KgsmTier>(_ => throw new DiscordAuthException("Discord unreachable."));

    /// <summary>How many times the authority was actually asked.</summary>
    private static IAuthorityProvider Authority(ISignInService seam) => (IAuthorityProvider)seam;

    private readonly string _dbDir = Path.Combine(
        Path.GetTempPath(), "kgsm-auth-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dbDir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private AuthService Build(
        ISignInService directory,
        out ISessionRegistry registry,
        out ISessionTokenService tokens,
        bool actionsEnabled = true,
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

        return new AuthService(
            directory,
            (IAuthorityProvider)directory,
            tokens,
            registry,
            new SessionValidator(registry, new MemoryCache(new MemoryCacheOptions()), TimeSpan.FromSeconds(5)),
            new KgsmTierCache(TimeSpan.FromSeconds(roleCacheTtlSeconds)),
            authOptions,
            assistantOptions,
            NullLogger<AuthService>.Instance);
    }

    private AuthService BuildWithBlankHostId(
        ISignInService directory, out ISessionRegistry registry, out ISessionTokenService tokens)
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

        return new AuthService(
            directory, (IAuthorityProvider)directory, tokens, registry,
            new SessionValidator(registry, new MemoryCache(new MemoryCacheOptions()), TimeSpan.FromSeconds(5)),
            new KgsmTierCache(TimeSpan.FromSeconds(60)),
            authOptions, assistantOptions, NullLogger<AuthService>.Instance);
    }

    private static AuthPrincipal Principal(string userId = "u1", string sessionId = "sid_x") =>
        new(KgsmActorProvider.Discord, userId, "Alice", sessionId);

    [Fact]
    public void TheAuthorizeUrlCarriesThisHandshakesChallengeAndNotItsVerifier()
    {
        var directory = Substitute.For<ISignInService, IAuthorityProvider>();
        AuthService service = Build(directory, out _, out _);
        var handshake = OAuthHandshake.Create();

        service.BuildAuthorizeUrl(handshake, prompt: null);

        // The verifier is the secret half — it stays in the browser's cookie and is presented only at
        // the token exchange. Only the derived challenge may travel through a URL.
        directory.Received(1).BuildAuthorizeUrl(handshake.State, handshake.CodeChallenge, "none");
    }

    [Fact]
    public async Task ALoginMintsAPairAndRecordsARevocableSession()
    {
        var directory = Substitute.For<ISignInService, IAuthorityProvider>();
        AuthService service = Build(directory, out ISessionRegistry registry, out _);

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
        var directory = Substitute.For<ISignInService, IAuthorityProvider>();
        AuthService service = BuildWithBlankHostId(directory, out ISessionRegistry registry, out ISessionTokenService tokens);

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
        var directory = Substitute.For<ISignInService, IAuthorityProvider>();
        AuthService service = Build(directory, out _, out _);

        await service.CreateSessionAsync(
            new ResolvedPrincipal(Alice, KgsmTier.Operator), null, CancellationToken.None);

        (await service.CanPerformActionsAsync(Principal())).Should().BeTrue();

        await Authority(directory).DidNotReceive().ResolveTierAsync(
            Arg.Any<KgsmIdentity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARefreshRotatesAndTheOldTokenStopsWorking()
    {
        // Reuse detection: a refresh token is single-use. Presenting a rotated-away one means either a
        // stale client or a stolen token, and there is no way to tell which — so neither is renewed.
        var directory = Substitute.For<ISignInService, IAuthorityProvider>();
        AuthService service = Build(directory, out _, out _);

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
        var directory = Substitute.For<ISignInService, IAuthorityProvider>();
        AuthService service = Build(directory, out _, out _);

        AuthSessionResult session = await service.CreateSessionAsync(
            new ResolvedPrincipal(Alice, KgsmTier.Operator), null, CancellationToken.None);

        (await service.RefreshAsync(session.AccessToken, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task LogoutKillsTheSessionAndItsRefreshToken()
    {
        var directory = Substitute.For<ISignInService, IAuthorityProvider>();
        AuthService service = Build(directory, out ISessionRegistry registry, out _);

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
        var directory = Substitute.For<ISignInService, IAuthorityProvider>();
        AuthService service = Build(directory, out _, out _);
        StubTier(directory, KgsmTier.Operator);

        (await service.CanPerformActionsAsync(Principal())).Should().BeTrue();
        (await service.CanPerformActionsAsync(Principal())).Should().BeTrue();

        await Authority(directory).Received(1).ResolveTierAsync(
            Arg.Any<KgsmIdentity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnIdentityThatProvesNoAccountDenies()
    {
        var directory = Substitute.For<ISignInService, IAuthorityProvider>();
        AuthService service = Build(directory, out _, out _);
        // Nobody has attached this identity to an account here. A stranger, whatever they hold
        // anywhere else.
        StubTier(directory, KgsmTier.None);

        // A KNOWN answer, and the answer is no — nothing about this is an outage.
        TierResolution resolved = await service.ResolveTierAsync(Principal());
        resolved.Known.Should().BeTrue();
        resolved.Tier.Should().Be(KgsmTier.None);
        (await service.CanPerformActionsAsync(Principal())).Should().BeFalse();
    }

    [Fact]
    public async Task AViewerAccountReadsAndCannotAct()
    {
        var directory = Substitute.For<ISignInService, IAuthorityProvider>();
        AuthService service = Build(directory, out _, out _);
        StubTier(directory, KgsmTier.Viewer);

        TierResolution resolved = await service.ResolveTierAsync(Principal());
        resolved.Known.Should().BeTrue();
        resolved.Tier.Should().Be(KgsmTier.Viewer);
        (await service.CanPerformActionsAsync(Principal())).Should().BeFalse();
    }

    [Fact]
    public async Task AnUnreachableDiscordDeniesTheCheckAndIsNotCachedAsAnAnswer()
    {
        // "We could not ask" is not "the answer is no". Caching the failure would turn a brief Discord
        // outage into a full-TTL lockout for someone who really is an operator.
        var directory = Substitute.For<ISignInService, IAuthorityProvider>();
        AuthService service = Build(directory, out _, out _);

        StubUnreachable(directory);

        (await service.CanPerformActionsAsync(Principal())).Should().BeFalse();

        StubTier(directory, KgsmTier.Operator);

        (await service.CanPerformActionsAsync(Principal())).Should().BeTrue();
    }

    [Fact]
    public async Task AnUnreachableDiscordResolvesToUnknownRatherThanADenial()
    {
        // The distinction the whole type exists for: an outage must not be reportable as a verdict.
        var directory = Substitute.For<ISignInService, IAuthorityProvider>();
        AuthService service = Build(directory, out _, out _);

        StubUnreachable(directory);

        TierResolution resolved = await service.ResolveTierAsync(Principal());
        resolved.Known.Should().BeFalse();

        // The review gate sees the same unknown, which is what lets it answer 502 instead of 403.
        (await service.ResolveReviewAuthorityAsync(Principal())).Known.Should().BeFalse();

        // And a caller that only wants a yes/no still gets a safe no — nobody is admitted in an outage.
        resolved.OrNone.Should().Be(KgsmTier.None);
        (await service.IsAdminAsync(Principal())).Should().BeFalse();
    }

    [Fact]
    public async Task AResolvedNonAdminIsAKnownDenial()
    {
        var directory = Substitute.For<ISignInService, IAuthorityProvider>();
        AuthService service = Build(directory, out _, out _);
        StubTier(directory, KgsmTier.Operator);

        TierResolution resolved = await service.ResolveReviewAuthorityAsync(Principal());

        resolved.Known.Should().BeTrue();
        resolved.Tier.Should().Be(KgsmTier.Operator);
    }

    // --- The review gate's HTTP answers ------------------------------------------------------------
    // What a client actually sees, which is where the outage/denial distinction has to survive: a
    // browser told 403 shows the operator a permissions problem that does not exist.

    private const string PassedThrough = "handler ran";

    /// <summary>Runs AdminOnlyFilter over a session-bearer request and reports what it answered.</summary>
    private static async Task<object?> RunReviewGateAsync(AuthService auth)
    {
        var http = new DefaultHttpContext();
        http.Items[BearerAuthFilter.PrincipalKey] = Principal();

        return await new AdminOnlyFilter(auth).InvokeAsync(
            EndpointFilterInvocationContext.Create(http),
            _ => ValueTask.FromResult<object?>(PassedThrough));
    }

    [Fact]
    public async Task ReviewGate_WhenDiscordIsUnreachable_Answers502AndNot403()
    {
        var directory = Substitute.For<ISignInService, IAuthorityProvider>();
        AuthService service = Build(directory, out _, out _);
        StubUnreachable(directory);

        object? answer = await RunReviewGateAsync(service);

        answer.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status502BadGateway);

        // The code the SPA branches on to say "couldn't check" rather than "unavailable". It also
        // separates this from a reverse proxy's own 502 for a dead leaf, which carries no envelope.
        JsonSerializer.Serialize(answer.As<IValueHttpResult>().Value)
            .Should().Contain(AdminOnlyFilter.UnavailableCode);
    }

    [Fact]
    public async Task ReviewGate_WhenTheCallerIsSimplyNotAnAdmin_Answers403()
    {
        var directory = Substitute.For<ISignInService, IAuthorityProvider>();
        AuthService service = Build(directory, out _, out _);
        StubTier(directory, KgsmTier.Operator);

        object? answer = await RunReviewGateAsync(service);

        answer.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task ReviewGate_WhenTheCallerHoldsTheReviewRole_RunsTheHandler()
    {
        var directory = Substitute.For<ISignInService, IAuthorityProvider>();
        AuthService service = Build(directory, out _, out _);
        StubTier(directory, KgsmTier.Admin);

        (await RunReviewGateAsync(service)).Should().Be(PassedThrough);
    }

    [Fact]
    public async Task ReviewNeedsAdminNotOperator()
    {
        var directory = Substitute.For<ISignInService, IAuthorityProvider>();
        AuthService service = Build(directory, out _, out _);
        StubTier(directory, KgsmTier.Operator);

        (await service.CanPerformActionsAsync(Principal())).Should().BeTrue();
        (await service.IsAdminAsync(Principal())).Should().BeFalse();
    }

    [Fact]
    public async Task AdminCanDoEverythingOperatorCan()
    {
        var directory = Substitute.For<ISignInService, IAuthorityProvider>();
        AuthService service = Build(directory, out _, out _);
        StubTier(directory, KgsmTier.Admin);

        (await service.IsAdminAsync(Principal())).Should().BeTrue();
        (await service.CanPerformActionsAsync(Principal())).Should().BeTrue();
    }

    [Fact]
    public async Task TheMasterSwitchDeniesWithoutCallingDiscord()
    {
        var directory = Substitute.For<ISignInService, IAuthorityProvider>();
        AuthService service = Build(directory, out _, out _, actionsEnabled: false);

        (await service.CanPerformActionsAsync(Principal())).Should().BeFalse();
        await Authority(directory).DidNotReceive().ResolveTierAsync(
            Arg.Any<KgsmIdentity>(), Arg.Any<CancellationToken>());
    }

    /// <summary>The session id a minted refresh token carries, read back the way the service reads it.</summary>
    private static async Task<string> SessionIdOf(AuthService service, string refreshToken)
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
