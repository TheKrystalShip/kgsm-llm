using FluentAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.Kgsm.Assistant.Extensions;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Extensions;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Retrieval;
using TheKrystalShip.Kgsm.Assistant.Ports;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Tests;

/// <summary>
/// The §D7 gating seam, same silent-failure shape as <see cref="WebSearchWiringTests"/>: only when
/// <c>Rag:Enabled</c> is true does <c>AddKgsmAdapters</c> register the concrete <see cref="RagRetrieval"/>,
/// which — given the real call order (<c>AddKgsmAssistant</c> THEN <c>AddKgsmAdapters</c>) — wins over
/// the library's fail-closed <c>DisabledRetrieval</c> default. When disabled, nothing is registered and
/// retrieval stays closed. Both directions fail silently if wired wrong (startup still succeeds), so a
/// DI-resolution assertion is the only thing that catches a regression.
/// </summary>
public class RagRetrievalWiringTests
{
    private static ServiceProvider BuildComposedProvider(bool ragEnabled)
    {
        var settings = new Dictionary<string, string?>
        {
            // AddKgsmAdapters throws on a missing KGSM section; nothing here binds the socket.
            ["KGSM:Path"] = "/opt/kgsm/kgsm.sh",
        };
        if (ragEnabled)
        {
            settings["Rag:Enabled"] = "true";
            settings["Rag:IndexPath"] = "/var/lib/kgsm/rag/index.krag"; // need not exist to resolve the graph
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging();          // the adapters' ctors need ILogger<>
        services.AddKgsmAssistant();    // TryAdd<IRetrieval, DisabledRetrieval> — the default
        services.AddKgsmAdapters(config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void When_enabled_IRetrieval_resolves_to_the_rag_adapter()
    {
        using var provider = BuildComposedProvider(ragEnabled: true);
        using var scope = provider.CreateScope();

        var resolved = scope.ServiceProvider.GetRequiredService<IRetrieval>();

        resolved.Should().BeOfType<RagRetrieval>(
            "with Rag:Enabled=true, AddKgsmAdapters' registration must win over the library's " +
            "TryAddSingleton<IRetrieval, DisabledRetrieval> default (last registration wins, given the order)");
    }

    [Fact]
    public async Task When_disabled_IRetrieval_stays_closed()
    {
        using var provider = BuildComposedProvider(ragEnabled: false);
        using var scope = provider.CreateScope();

        var resolved = scope.ServiceProvider.GetRequiredService<IRetrieval>();
        resolved.Should().NotBeOfType<RagRetrieval>("no adapter is registered when Rag is disabled");

        var result = await resolved.RetrieveAsync("anything");
        result.IsFailure.Should().BeTrue("the fail-closed default must omit retrieval, not answer it");
    }
}
