using FluentAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Extensions;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Extensions;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Fetch;
using TheKrystalShip.Kgsm.Assistant.Ports;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Tests;

/// <summary>
/// Verifies the composition root every host actually uses (mirrors <c>WebSearchWiringTests</c>): the
/// concrete <see cref="HttpWebFetch"/> (registered by <c>AddKgsmAdapters</c>) must win over the
/// assistant library's fail-closed <c>DisabledWebFetch</c> default (<c>TryAdd</c>'d by
/// <c>AddKgsmAssistant</c>), AND <c>FetchOptions.Available</c> must reflect <c>WebFetch:Enabled</c>
/// — the flag <see cref="ServerAssistant.SelectTools"/> reads to decide whether <c>fetch_url</c> is
/// offered at all. The failure mode is SILENT: if Disabled ever resolved, or Available stayed false
/// with Enabled=true, startup would still succeed and every other test would stay green.
/// <para>
/// CRITICAL: the test must reproduce the real call ORDER (<c>AddKgsmAssistant</c> THEN
/// <c>AddKgsmAdapters</c>) — that ordering IS the thing under test.
/// </para>
/// </summary>
public class WebFetchWiringTests
{
    private static ServiceProvider BuildComposedProvider(bool enabled)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KGSM:Path"] = "/opt/kgsm/kgsm.sh",
                ["WebFetch:Enabled"] = enabled ? "true" : "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddKgsmAssistant();      // TryAdd<IWebFetch, DisabledWebFetch> — the default that must LOSE when enabled
        services.AddKgsmAdapters(config); // AddHttpClient<IWebFetch, HttpWebFetch> — must WIN when enabled
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Enabled_IWebFetch_ResolvesToHttpWebFetch_NotTheFailClosedDefault()
    {
        using var provider = BuildComposedProvider(enabled: true);
        using var scope = provider.CreateScope();

        var resolved = scope.ServiceProvider.GetRequiredService<IWebFetch>();

        resolved.Should().BeOfType<HttpWebFetch>(
            "AddKgsmAdapters' AddHttpClient<IWebFetch, HttpWebFetch> must win over the library's " +
            "TryAddSingleton<IWebFetch, DisabledWebFetch> default (last registration wins, given the order)");
    }

    [Fact]
    public void Enabled_FetchOptions_Available_IsTrue()
    {
        using var provider = BuildComposedProvider(enabled: true);

        provider.GetRequiredService<IOptions<FetchOptions>>().Value.Available.Should().BeTrue();
    }

    [Fact]
    public void Disabled_FetchOptions_Available_IsFalse()
    {
        using var provider = BuildComposedProvider(enabled: false);

        provider.GetRequiredService<IOptions<FetchOptions>>().Value.Available.Should().BeFalse();
    }

    [Fact]
    public void DailyFetchBudget_IsSingleton()
    {
        using var provider = BuildComposedProvider(enabled: true);

        var first = provider.GetRequiredService<DailyFetchBudget>();
        var second = provider.GetRequiredService<DailyFetchBudget>();

        first.Should().BeSameAs(second);
    }
}
