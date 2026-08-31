using FluentAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Kgsm.Assistant.Service.Cluster;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

/// <summary>
/// Which engine this service reaches, decided once at startup by whether there is a cluster.
/// </summary>
/// <remarks>
/// <para>
/// <b>A machine standing alone is the deployment most installs are, and it must not change.</b> The
/// leaf doctrine rests on it: a leaf runs fully functional beside its own engine, depending on
/// nothing else. The clustered adapters are registered after the kgsm-lib ones and only when there is
/// a secret, so a standalone install resolves exactly what it always did — but "registered later,
/// conditionally" is invisible at a glance and silent when it goes wrong, which is why it is pinned
/// here rather than trusted.
/// </para>
/// <para>
/// The failure this catches is the bad one in both directions: a standalone install reaching for a
/// Control Panel API that is not installed, or a member of a cluster shelling out to an engine that
/// answers for one machine out of several.
/// </para>
/// </remarks>
public sealed class StandingSelectionTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("kgsm-standing-tests").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// No secret, so there is no cluster to serve and the engine beside this machine is the only one
    /// there is. Every port resolves to the kgsm-lib adapter, which is what makes this leaf work with
    /// no other ecosystem service present.
    /// </summary>
    [Fact]
    public void A_machine_standing_alone_reaches_its_own_engine()
    {
        using WebApplicationFactory<Program> service = Service(secret: "");
        using IServiceScope scope = service.Services.CreateScope();

        Resolved<IServerInventory>(scope).Should().NotBeOfType<ClusterServerInventory>();
        Resolved<IServerOperations>(scope).Should().NotBeOfType<ClusterServerOperations>();
        Resolved<IServerFacts>(scope).Should().NotBeOfType<ClusterServerFacts>();
        Resolved<IServerMetrics>(scope).Should().NotBeOfType<ClusterServerMetrics>();
        Resolved<INetworkInfo>(scope).Should().NotBeOfType<ClusterNetworkInfo>();
        Resolved<IUpnpInfo>(scope).Should().NotBeOfType<ClusterUpnpInfo>();
        Resolved<IEventHistory>(scope).Should().NotBeOfType<ClusterEventHistory>();
        Resolved<IHostFacts>(scope).Should().NotBeOfType<ClusterHostFacts>();
    }

    /// <summary>
    /// A secret, so this member serves a cluster and has no standing to reach any node's engine
    /// directly — including the one on its own machine, because a shortcut for the local case is a
    /// second set of answers about one host with nothing saying which of the two somebody got.
    /// </summary>
    [Fact]
    public void A_member_of_a_cluster_reaches_the_nodes()
    {
        using WebApplicationFactory<Program> service = Service(secret: "a-cluster-secret");
        using IServiceScope scope = service.Services.CreateScope();

        Resolved<IServerInventory>(scope).Should().BeOfType<ClusterServerInventory>();
        Resolved<IServerOperations>(scope).Should().BeOfType<ClusterServerOperations>();
        Resolved<IServerFacts>(scope).Should().BeOfType<ClusterServerFacts>();
        Resolved<IServerMetrics>(scope).Should().BeOfType<ClusterServerMetrics>();
        Resolved<INetworkInfo>(scope).Should().BeOfType<ClusterNetworkInfo>();
        Resolved<IUpnpInfo>(scope).Should().BeOfType<ClusterUpnpInfo>();
        Resolved<IEventHistory>(scope).Should().BeOfType<ClusterEventHistory>();
        Resolved<IHostFacts>(scope).Should().BeOfType<ClusterHostFacts>();
    }

    /// <summary>
    /// The invalidation seam follows the inventory rather than being registered beside it, so the
    /// engine's own events on this machine drop what the fleet said instead of a cache nothing reads.
    /// </summary>
    [Fact]
    public void Invalidating_reaches_the_inventory_that_is_actually_read()
    {
        using WebApplicationFactory<Program> service = Service(secret: "a-cluster-secret");
        using IServiceScope scope = service.Services.CreateScope();

        object inventory = Resolved<IServerInventory>(scope);
        object invalidation = scope.ServiceProvider.GetRequiredService<
            Kgsm.Assistant.Infrastructure.Kgsm.IInventoryInvalidation>();

        invalidation.Should().BeSameAs(inventory);
    }

    private static object Resolved<T>(IServiceScope scope) where T : notnull =>
        scope.ServiceProvider.GetRequiredService<T>();

    /// <summary>This service on its own files. A blank secret is a machine that is not in a cluster.</summary>
    private WebApplicationFactory<Program> Service(string secret) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("KGSM:Path", "/opt/kgsm/kgsm.sh");
            builder.UseSetting("Auth:SigningKey", "standing-selection-signing-key");
            builder.UseSetting("Auth:HostId", "test-assistant");
            builder.UseSetting("Auth:UsersDbPath", Path.Combine(_directory, "users.db"));
            builder.UseSetting("Conversation:DatabasePath", Path.Combine(_directory, "conversations.db"));
            builder.UseSetting("Cluster:Secret", secret);
            builder.UseSetting("Cluster:MemberId", "test-assistant");
        });
}
