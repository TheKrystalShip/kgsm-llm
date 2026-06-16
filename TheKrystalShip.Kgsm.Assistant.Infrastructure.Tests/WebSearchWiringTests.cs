using FluentAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.Kgsm.Assistant.Extensions;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Extensions;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Search;
using TheKrystalShip.Kgsm.Assistant.Ports;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Tests;

/// <summary>
/// Verifies the composition root every host actually uses — the seam unit tests that <c>new</c>
/// their types can't see. The web_search design hinges on the concrete <see cref="TavilyWebSearch"/>
/// (registered by <c>AddKgsmAdapters</c>) winning over the assistant library's fail-closed
/// <c>DisabledWebSearch</c> default (TryAdd'd by <c>AddKgsmAssistant</c>). The failure mode is SILENT:
/// if Disabled ever resolved, startup still succeeds and every search returns "not configured", and
/// every other test stays green. Only a DI-resolution assertion catches it.
/// <para>
/// CRITICAL: the test must reproduce the real call ORDER (<c>AddKgsmAssistant</c> THEN
/// <c>AddKgsmAdapters</c>) — that ordering IS the thing under test. Calling <c>AddKgsmAdapters</c>
/// alone would pass trivially and stop guarding the override. No API key needed → runs in CI;
/// resolving <see cref="IWebSearch"/> pulls only the HttpClient + options + budget, never the
/// socket-bound kgsm graph, so this stays a fast unit test.
/// </para>
/// </summary>
public class WebSearchWiringTests
{
    private static ServiceProvider BuildComposedProvider()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // AddKgsmAdapters throws on a missing KGSM section; nothing here binds the socket.
                ["KGSM:Path"] = "/opt/kgsm/kgsm.sh",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();          // TavilyWebSearch ctor needs ILogger<> (the web host gave this for free)
        services.AddKgsmAssistant();    // TryAdd<IWebSearch, DisabledWebSearch> — the default that must LOSE
        services.AddKgsmAdapters(config); // AddHttpClient<IWebSearch, TavilyWebSearch> — must WIN
        return services.BuildServiceProvider();
    }

    [Fact]
    public void IWebSearch_Resolves_ToTavilyAdapter_NotTheFailClosedDefault()
    {
        using var provider = BuildComposedProvider();
        using var scope = provider.CreateScope();

        var resolved = scope.ServiceProvider.GetRequiredService<IWebSearch>();

        resolved.Should().BeOfType<TavilyWebSearch>(
            "AddKgsmAdapters' AddHttpClient<IWebSearch, TavilyWebSearch> must win over the library's " +
            "TryAddSingleton<IWebSearch, DisabledWebSearch> default (last registration wins, given the order)");
    }

    [Fact]
    public void DailyCallBudget_IsSingleton()
    {
        // The wallet cap must be process-global; a transient would silently reset the count per
        // resolution and defeat the only spend guard in front of a publicly-offered tool.
        using var provider = BuildComposedProvider();

        var first = provider.GetRequiredService<DailyCallBudget>();
        var second = provider.GetRequiredService<DailyCallBudget>();

        first.Should().BeSameAs(second);
    }
}
