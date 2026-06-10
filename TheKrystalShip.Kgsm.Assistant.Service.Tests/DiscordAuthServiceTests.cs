using System.Security.Cryptography;
using System.Text;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Service.Configuration;
using TheKrystalShip.Kgsm.Assistant.Service.Discord;
using TheKrystalShip.Kgsm.Assistant.Service.Security;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

public class DiscordAuthServiceTests
{
    private const string ActionRole = "role-1";

    private static DiscordAuthService Build(
        IDiscordOAuthClient discord,
        out SessionStore sessions,
        out OAuthStateStore states,
        out RoleCache roleCache,
        bool actionsEnabled = true,
        string actionRoleId = ActionRole)
    {
        var auth = Options.Create(new AuthOptions { SessionTtlSeconds = 3600, RoleCacheTtlSeconds = 60, StateTtlSeconds = 300 });
        sessions = new SessionStore();
        states = new OAuthStateStore(auth);
        roleCache = new RoleCache(auth);

        var discordOpts = Options.Create(new DiscordOAuthOptions
        {
            ClientId = "client-id",
            RedirectUri = "https://spa.example/callback",
            Scopes = "identify guilds.members.read",
            GuildId = "guild-1",
            ActionRoleId = actionRoleId,
        });
        var assistantOpts = Options.Create(new AssistantServiceOptions
        {
            ActionsEnabled = actionsEnabled,
            Confirmation = new ConfirmationOptions { Key = "signing-key" },
        });
        var tokens = new ConfirmationTokenService(assistantOpts);

        return new DiscordAuthService(
            discord, sessions, states, roleCache, tokens,
            discordOpts, auth, assistantOpts, NullLogger<DiscordAuthService>.Instance);
    }

    private static string QueryValue(string url, string key)
    {
        var query = new Uri(url).Query.TrimStart('?');
        foreach (var pair in query.Split('&'))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0] == key)
                return Uri.UnescapeDataString(kv[1]);
        }
        return string.Empty;
    }

    [Fact]
    public void BuildLoginUrl_EmitsCorrectPkceChallengeForTheStoredVerifier()
    {
        var service = Build(Substitute.For<IDiscordOAuthClient>(), out _, out var states, out _);

        var url = service.BuildLoginUrl();

        QueryValue(url, "code_challenge_method").Should().Be("S256");
        QueryValue(url, "response_type").Should().Be("code");

        var state = QueryValue(url, "state");
        var challenge = QueryValue(url, "code_challenge");

        states.TryConsume(state, out var verifier).Should().BeTrue();
        var expected = Base64Url.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        challenge.Should().Be(expected);
    }

    [Fact]
    public async Task CompleteLogin_MemberWithRole_CreatesSession_AndSeedsAuthority()
    {
        var discord = Substitute.For<IDiscordOAuthClient>();
        var service = Build(discord, out var sessions, out var states, out _);

        // A real handshake so the state/verifier exist.
        var state = QueryValue(service.BuildLoginUrl(), "state");
        discord.ExchangeCodeAsync("code", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DiscordTokenResponse { AccessToken = "access" });
        discord.GetGuildMemberAsync("access", Arg.Any<CancellationToken>())
            .Returns(new DiscordGuildMember { Roles = new[] { ActionRole }, User = new DiscordUser { Id = "u1", Username = "Alice" } });

        var result = await service.CompleteLoginAsync("code", state);

        result.Should().NotBeNull();
        result!.DisplayName.Should().Be("Alice");
        sessions.TryGet(result.SessionToken, out _).Should().BeTrue();

        // Authority was seeded from the member we already fetched — a re-check is served from
        // cache without a second Discord call.
        service.TryResolvePrincipal(result.SessionToken, out var principal).Should().BeTrue();
        (await service.CanPerformActionsAsync(principal)).Should().BeTrue();
        await discord.Received(1).GetGuildMemberAsync("access", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteLogin_NotAGuildMember_IsDenied()
    {
        var discord = Substitute.For<IDiscordOAuthClient>();
        var service = Build(discord, out var sessions, out _, out _);

        var state = QueryValue(service.BuildLoginUrl(), "state");
        discord.ExchangeCodeAsync("code", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DiscordTokenResponse { AccessToken = "access" });
        discord.GetGuildMemberAsync("access", Arg.Any<CancellationToken>())
            .Returns((DiscordGuildMember?)null); // 404 → not a member

        var result = await service.CompleteLoginAsync("code", state);

        result.Should().BeNull();
    }

    [Fact]
    public async Task CompleteLogin_BadState_IsRejected_WithoutExchanging()
    {
        var discord = Substitute.For<IDiscordOAuthClient>();
        var service = Build(discord, out _, out _, out _);

        var result = await service.CompleteLoginAsync("code", "never-issued-state");

        result.Should().BeNull();
        await discord.DidNotReceive().ExchangeCodeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CanPerformActions_CachesWithinTtl()
    {
        var discord = Substitute.For<IDiscordOAuthClient>();
        var service = Build(discord, out var sessions, out _, out _);
        var token = sessions.Create(new Session("u1", "U", "access", DateTimeOffset.UtcNow.AddHours(1)));
        service.TryResolvePrincipal(token, out var principal);

        discord.GetGuildMemberAsync("access", Arg.Any<CancellationToken>())
            .Returns(new DiscordGuildMember { Roles = new[] { ActionRole }, User = new DiscordUser { Id = "u1" } });

        (await service.CanPerformActionsAsync(principal)).Should().BeTrue();
        (await service.CanPerformActionsAsync(principal)).Should().BeTrue();

        await discord.Received(1).GetGuildMemberAsync("access", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CanPerformActions_TokenExpired_EvictsSession()
    {
        var discord = Substitute.For<IDiscordOAuthClient>();
        var service = Build(discord, out var sessions, out _, out _);
        var token = sessions.Create(new Session("u1", "U", "access", DateTimeOffset.UtcNow.AddHours(1)));
        service.TryResolvePrincipal(token, out var principal);

        discord.GetGuildMemberAsync("access", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<DiscordGuildMember?>(new DiscordTokenExpiredException("401")));

        (await service.CanPerformActionsAsync(principal)).Should().BeFalse();
        sessions.TryGet(token, out _).Should().BeFalse(); // evicted → forces re-login
    }

    [Fact]
    public async Task CanPerformActions_MasterSwitchOff_DeniesWithoutCallingDiscord()
    {
        var discord = Substitute.For<IDiscordOAuthClient>();
        var service = Build(discord, out var sessions, out _, out _, actionsEnabled: false);
        var token = sessions.Create(new Session("u1", "U", "access", DateTimeOffset.UtcNow.AddHours(1)));
        service.TryResolvePrincipal(token, out var principal);

        (await service.CanPerformActionsAsync(principal)).Should().BeFalse();
        await discord.DidNotReceive().GetGuildMemberAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CanPerformActions_NoActionRoleConfigured_Denies()
    {
        var discord = Substitute.For<IDiscordOAuthClient>();
        var service = Build(discord, out var sessions, out _, out _, actionRoleId: "");
        var token = sessions.Create(new Session("u1", "U", "access", DateTimeOffset.UtcNow.AddHours(1)));
        service.TryResolvePrincipal(token, out var principal);

        (await service.CanPerformActionsAsync(principal)).Should().BeFalse();
        await discord.DidNotReceive().GetGuildMemberAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
