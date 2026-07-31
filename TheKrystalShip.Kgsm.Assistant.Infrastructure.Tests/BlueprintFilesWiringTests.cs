using FluentAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.Kgsm.Assistant.Extensions;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Extensions;
using TheKrystalShip.KGSM.Core.Interfaces;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Tests;

/// <summary>
/// <c>IBlueprintFiles</c> is <c>create_blueprint</c>'s write-side authority, and it comes from
/// kgsm-lib — so its constructor dependencies change under us on a lib bump, silently. Nothing
/// fails to compile: the graph builds, the service starts, and the first blueprint write is what
/// throws. A resolution assertion is the only thing that catches it before a user does.
/// </summary>
public class BlueprintFilesWiringTests
{
    private static ServiceProvider BuildComposedProvider()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["KGSM:Path"] = "/opt/kgsm/kgsm.sh" })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddKgsmAssistant();
        services.AddKgsmAdapters(config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void The_blueprint_write_authority_resolves_with_every_dependency()
    {
        using var provider = BuildComposedProvider();

        provider.Invoking(p => p.GetRequiredService<IBlueprintFiles>()).Should().NotThrow();
    }

    /// <summary>
    /// A blueprint write is announced by the engine's own <c>kgsm events emit</c>, which the write
    /// authority reaches through this service — shelling out like every other read and write here,
    /// binding no socket.
    /// </summary>
    [Fact]
    public void The_emit_seam_resolves()
    {
        using var provider = BuildComposedProvider();

        provider.GetService<IEventManagementService>().Should().NotBeNull();
    }
}
