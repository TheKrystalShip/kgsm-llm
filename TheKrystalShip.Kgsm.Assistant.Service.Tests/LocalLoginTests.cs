using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.Kgsm.Assistant.Service.Security;
using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Users;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

/// <summary>
/// <c>POST /auth/login</c> — signing in to the assistant with a KGSM password.
/// </summary>
/// <remarks>
/// <para>
/// The account store is real (a temp file per run, never the host's) because most of what this
/// endpoint promises is enforced below the endpoint: the single answer to a bad username or a bad
/// password, the lockout, the tier an account actually holds.
/// </para>
/// <para>
/// <b>Nothing here substitutes the sign-in seam.</b> That is the point of the whole surface — this
/// leaf authenticates somebody with no identity provider reachable, or configured, at all.
/// </para>
/// </remarks>
public sealed class LocalLoginTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Password = "correct-horse-battery-staple";

    private readonly string _storeDirectory = Path.Combine(
        Path.GetTempPath(), "kgsm-assistant-login-" + Guid.NewGuid().ToString("N"));

    private readonly WebApplicationFactory<Program> _app;

    public LocalLoginTests(WebApplicationFactory<Program> factory)
    {
        Directory.CreateDirectory(_storeDirectory);

        _app = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("KGSM:Path", "/opt/kgsm/kgsm.sh");
            builder.UseSetting("Auth:SigningKey", "local-login-signing-key");
            builder.UseSetting("Auth:HostId", "test-host");
            // This run's own accounts, never the host's shared store.
            builder.UseSetting("Auth:UsersDbPath", Path.Combine(_storeDirectory, "users.db"));
            builder.UseSetting("Conversation:DatabasePath",
                Path.Combine(_storeDirectory, "conversations.db"));
        });
    }

    private UserDirectory Users => _app.Services.GetRequiredService<UserDirectory>();

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    private async Task<KgsmUser> Enrol(
        string username, KgsmTier tier = KgsmTier.Operator, UserStatus status = UserStatus.Active)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        KgsmUser user = new(
            UserIds.NewUserId(), username, username, tier, TierSource.Granted, status, now, now);

        await Users.Store.CreateAsync(user);
        await Users.SignIn.SetPasswordAsync(user.UserId, Password, now);
        return user;
    }

    private Task<HttpResponseMessage> Login(string? username, string? password) =>
        _app.CreateClient().PostAsJsonAsync("/auth/login", new LoginRequest(username, password));

    [Fact]
    public async Task ThePasswordDoorNeedsNoIdentityProvider()
    {
        // The whole reason the account store exists: this leaf signs somebody in standalone, with no
        // Discord application configured and nothing external reachable.
        string name = Unique("haru");
        KgsmUser user = await Enrol(name, KgsmTier.Admin);

        using HttpResponseMessage response = await Login(name, Password);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        AuthSessionResponse? body = await response.Content.ReadFromJsonAsync<AuthSessionResponse>();
        body!.Verdict.Should().Be("ok");
        body.Tier.Should().Be(KgsmTiers.Admin);
        body.UserId.Should().Be(user.UserId);
        body.Token.Should().NotBeNullOrEmpty();
        body.Refresh.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task TheMintedBearerIsAcceptedAndCarriesTheAccountsAuthority()
    {
        // The end-to-end assertion: the bearer travels the real validation path, and the tier comes
        // back re-derived from the account store rather than read off the token.
        string name = Unique("kaito");
        await Enrol(name, KgsmTier.Operator);

        using HttpResponseMessage login = await Login(name, Password);
        AuthSessionResponse session = (await login.Content.ReadFromJsonAsync<AuthSessionResponse>())!;

        HttpClient client = _app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", session.Token);

        using HttpResponseMessage me = await client.GetAsync("/auth/me");
        me.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument body = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("tier").GetString().Should().Be(KgsmTiers.Operator);
    }

    [Fact]
    public async Task ATierChangedInTheStoreTakesEffectWithoutANewSignIn()
    {
        // Authority is re-derived per request here, never baked into the bearer. The account store
        // being the source is what makes a demotion land on a session already open.
        string name = Unique("demoted");
        KgsmUser user = await Enrol(name, KgsmTier.Operator);

        using HttpResponseMessage login = await Login(name, Password);
        AuthSessionResponse session = (await login.Content.ReadFromJsonAsync<AuthSessionResponse>())!;

        HttpClient client = _app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", session.Token);

        await Users.Store.UpdateAsync(user with { Tier = KgsmTier.Viewer });
        // The tier cache is seeded at login and holds for its TTL, so drop the cached answer the way
        // a logout would rather than waiting a minute for it to lapse.
        _app.Services.GetRequiredService<KgsmTierCache>().Remove(user.AsIdentity().Handle);

        using HttpResponseMessage me = await client.GetAsync("/auth/me");
        using JsonDocument body = JsonDocument.Parse(await me.Content.ReadAsStringAsync());

        body.RootElement.GetProperty("tier").GetString().Should().Be(KgsmTiers.Viewer);
    }

    [Fact]
    public async Task AnAccountAwaitingApprovalSignsInAndHoldsNothing()
    {
        string name = Unique("pending");
        await Enrol(name, KgsmTier.Admin, UserStatus.Pending);

        using HttpResponseMessage response = await Login(name, Password);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        AuthSessionResponse body = (await response.Content.ReadFromJsonAsync<AuthSessionResponse>())!;
        body.Tier.Should().Be(KgsmTiers.None);
    }

    [Fact]
    public async Task AWrongPasswordAndAnUnknownUsernameAreTheSameAnswer()
    {
        // Two answers here is a username oracle. The bodies must match, not merely the status codes.
        string name = Unique("haru");
        await Enrol(name);

        using HttpResponseMessage wrongPassword = await Login(name, "not-the-password");
        using HttpResponseMessage noSuchUser = await Login(Unique("ghost"), Password);

        wrongPassword.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        noSuchUser.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await wrongPassword.Content.ReadAsStringAsync())
            .Should().Be(await noSuchUser.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ADisabledAccountIsOnlyToldSoOnceThePasswordIsRight()
    {
        string name = Unique("gone");
        await Enrol(name, KgsmTier.Admin, UserStatus.Disabled);

        using HttpResponseMessage guessed = await Login(name, "not-the-password");
        guessed.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using HttpResponseMessage known = await Login(name, Password);
        known.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCode(known)).Should().Be("account_disabled");
    }

    [Fact]
    public async Task EnoughWrongPasswordsLockTheAccountAndSayForHowLong()
    {
        string name = Unique("bruteforced");
        await Enrol(name);

        LockoutPolicy policy = LockoutPolicy.Default;
        for (int i = 0; i <= policy.Threshold; i++)
        {
            using HttpResponseMessage _ = await Login(name, "wrong");
        }

        using HttpResponseMessage locked = await Login(name, Password);

        locked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await ErrorCode(locked)).Should().Be("too_many_attempts");
        locked.Headers.RetryAfter!.Delta.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("haru", null)]
    [InlineData(null, "hunter2")]
    public async Task AMissingFieldIsARefusalAndNotAServerError(string? username, string? password)
    {
        using HttpResponseMessage response = await Login(username, password);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCode(response)).Should().Be("bad_request");
    }

    private static async Task<string?> ErrorCode(HttpResponseMessage response)
    {
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("error").GetString();
    }

    public void Dispose()
    {
        _app.Dispose();
        try
        {
            Directory.Delete(_storeDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a green test over.
        }
    }
}
