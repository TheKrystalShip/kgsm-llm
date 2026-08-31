using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using FluentAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Cluster;
using TheKrystalShip.KGSM.Auth.Sessions;
using TheKrystalShip.KGSM.Auth.Users;
using TheKrystalShip.KGSM.Cluster;
using TheKrystalShip.KGSM.Cluster.Membership;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

/// <summary>
/// The doors this service stops answering once another member holds the cluster's accounts, and the
/// sessions it starts accepting instead.
/// </summary>
/// <remarks>
/// <para>
/// Both halves are here because they are one decision seen from two sides: a member stops minting a
/// credential no other member accepts, and starts accepting the one every member does. Testing either
/// alone leaves a state where nobody can sign in anywhere.
/// </para>
/// <para>
/// The anchor is stood in for by a signer and a stub of what gossip would have delivered. What is
/// under test is this member's half of the contract; the other half has its own tests where it is
/// written.
/// </para>
/// </remarks>
public sealed class ClusterDoorTests : IDisposable
{
    private const string ClusterId = "test-cluster";

    /// <summary>The anchor's issuer, deliberately not this service's own.</summary>
    private const string AnchorIssuer = "kgsm";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "kgsm-assistant-doors-" + Guid.NewGuid().ToString("N"));

    public ClusterDoorTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* a temp dir that outlives a run costs nothing */ }
    }

    /// <summary>What gossip would have delivered from the member holding the accounts.</summary>
    private sealed record Published(string? Audience, string? Issuer, IReadOnlyList<SecurityKey> Keys)
        : IClusterSessionKeys
    {
        public static Published Of(EcdsaSessionSigner signer) =>
            new(ClusterId, AnchorIssuer, EcdsaSessionSigner.VerificationKeysFrom(signer.PublicKeys));
    }

    /// <summary>
    /// This service, configured onto its own files. <paramref name="secret"/> blank is a machine that
    /// is not in a cluster, which is the deployment most installs are and the one that must not change.
    /// </summary>
    private WebApplicationFactory<Program> Service(
        string secret = "", IClusterSessionKeys? published = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("KGSM:Path", "/opt/kgsm/kgsm.sh");
            builder.UseSetting("Auth:SigningKey", "cluster-door-signing-key");
            builder.UseSetting("Auth:HostId", "test-assistant");
            builder.UseSetting("Auth:UsersDbPath", Path.Combine(_directory, "users.db"));
            builder.UseSetting("Conversation:DatabasePath", Path.Combine(_directory, "conversations.db"));
            builder.UseSetting("Cluster:Secret", secret);
            builder.UseSetting("Cluster:MemberId", "test-assistant");

            if (published is not null)
            {
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IClusterSessionKeys>();
                    services.AddSingleton(published);
                });
            }
        });

    /// <summary>Record that another member holds the accounts, as gossip would have.</summary>
    private static async Task HolderIsElsewhereAsync(IServiceProvider services)
    {
        var state = services.GetRequiredService<ClusterStateStore>();
        await state.TryClaimAsync(ClusterCapability.Auth, "somebody-else", default);
    }

    private static SessionTokenService Anchor(EcdsaSessionSigner signer) =>
        new(new SessionTokenOptions(
                HostId: ClusterId,
                SigningKey: "",
                AccessLifetime: TimeSpan.FromMinutes(15),
                RefreshLifetime: TimeSpan.FromDays(30),
                Issuer: AnchorIssuer),
            logger: null,
            signer: signer);

    private static HttpRequestMessage Get(string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    [Fact]
    public async Task A_machine_that_is_not_in_a_cluster_answers_its_own_doors()
    {
        // The deployment most installs are, and the one this must not disturb. A wrong password is a
        // wrong password here, not a refusal to have the conversation.
        using WebApplicationFactory<Program> service = Service();
        using HttpClient client = service.CreateClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/auth/login", new { username = "nobody", password = "wrong" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.Contains(AnchorHeld.HolderHeader).Should().BeFalse();
    }

    [Fact]
    public async Task A_door_that_belongs_to_the_holder_is_closed_and_names_it()
    {
        using WebApplicationFactory<Program> service = Service(secret: "a-shared-cluster-secret");
        using HttpClient client = service.CreateClient();
        await HolderIsElsewhereAsync(service.Services);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/auth/login", new { username = "nobody", password = "wrong" });

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.GetValues(AnchorHeld.HolderHeader).Should().Equal("somebody-else");
    }

    [Fact]
    public async Task Refreshing_a_session_is_the_holder_s_too()
    {
        // Extending a sign-in is minting: the pair it hands back is a new credential no other member
        // would accept, which is the same fact as signing in for the first time.
        using WebApplicationFactory<Program> service = Service(secret: "a-shared-cluster-secret");
        using HttpClient client = service.CreateClient();
        await HolderIsElsewhereAsync(service.Services);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/auth/session/refresh", new { refresh = "anything" });

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task A_session_the_anchor_minted_is_accepted_and_resolved_from_this_member_s_own_replica()
    {
        using var signer = EcdsaSessionSigner.Generate();
        using WebApplicationFactory<Program> service =
            Service(secret: "a-shared-cluster-secret", published: Published.Of(signer));
        using HttpClient client = service.CreateClient();
        await HolderIsElsewhereAsync(service.Services);

        // The account is here because replication put it here, not because anybody signed in.
        var store = new SqliteUserStore(new UserStoreOptions { Path = Path.Combine(_directory, "users.db") });
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var account = new KgsmUser(
            UserIds.NewUserId(), "replicated", "Replicated",
            KgsmTier.Operator, TierSource.Granted, UserStatus.Active, now, now);
        await store.CreateAsync(account, default);
        // The identity a session names, carried by replication precisely so a session naming it
        // resolves on a member that never saw the person. No secret travels with it.
        await store.AddCredentialAsync(new UserCredential(
            UserIds.NewCredentialId(), account.UserId, CredentialKind.Identity,
            KgsmActor.Format(KgsmActorProvider.Discord, "9001"), null, "Replicated", now, null), default);

        var person = new KgsmIdentity(KgsmActorProvider.Discord, "9001", "9001", "Replicated", null, []);
        string token = Anchor(signer).MintAccess(person, KgsmTier.Admin, "sid_from_anchor").Token;

        HttpResponseMessage response = await client.SendAsync(Get("/auth/me", token));

        // Accepted on a signature this member cannot produce, for a session it has no row for.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_session_the_cluster_has_ended_is_refused_here()
    {
        using var signer = EcdsaSessionSigner.Generate();
        using WebApplicationFactory<Program> service =
            Service(secret: "a-shared-cluster-secret", published: Published.Of(signer));
        using HttpClient client = service.CreateClient();
        await HolderIsElsewhereAsync(service.Services);

        var person = new KgsmIdentity(KgsmActorProvider.Discord, "9002", "9002", "Ended", null, []);
        string token = Anchor(signer).MintAccess(person, KgsmTier.Admin, "sid_ended").Token;

        // What arriving over the bus does: a session with no row here is recorded as over, because the
        // signature alone would otherwise go on admitting it for the rest of its life.
        await service.Services.GetRequiredService<IClusterSessionAuthority>()
            .RecordRevocationAsync("sid_ended", DateTimeOffset.UtcNow.AddDays(1), default);

        HttpResponseMessage response = await client.SendAsync(Get("/auth/me", token));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
