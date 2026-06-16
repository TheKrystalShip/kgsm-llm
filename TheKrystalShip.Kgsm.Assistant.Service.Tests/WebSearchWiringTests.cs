using FluentAssertions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Kgsm.Assistant.Service.Search;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

/// <summary>
/// Verifies the composition root the running app actually uses — the seam unit tests that
/// <c>new</c> their types can't see. The web_search design hinges on the concrete
/// <see cref="TavilyWebSearch"/> (registered by the host) winning over the assistant library's
/// fail-closed <c>DisabledWebSearch</c> default. The failure mode is SILENT: if Disabled ever
/// resolved, startup still succeeds and every search returns "not configured", and every other
/// test stays green. Only a DI-resolution assertion catches it. No API key needed → runs in CI.
/// </summary>
public class WebSearchWiringTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public WebSearchWiringTests(WebApplicationFactory<Program> factory) =>
        // Provide kgsm settings so app startup binds the KGSM section regardless of whether the
        // test host discovers the service's appsettings.json (mirrors EndpointSmokeTests).
        _factory = factory.WithWebHostBuilder(b => b.UseSetting("KGSM:Path", "/opt/kgsm/kgsm.sh"));

    [Fact]
    public void IWebSearch_Resolves_ToTavilyAdapter_NotTheFailClosedDefault()
    {
        using var scope = _factory.Services.CreateScope();

        var resolved = scope.ServiceProvider.GetRequiredService<IWebSearch>();

        resolved.Should().BeOfType<TavilyWebSearch>(
            "the host's AddHttpClient<IWebSearch, TavilyWebSearch> must win over the library's " +
            "TryAddSingleton<IWebSearch, DisabledWebSearch> default");
    }

    [Fact]
    public void DailyCallBudget_IsSingleton()
    {
        // The wallet cap must be process-global; a transient would silently reset the count per
        // resolution and defeat the only spend guard in front of a publicly-offered tool.
        var first = _factory.Services.GetRequiredService<DailyCallBudget>();
        var second = _factory.Services.GetRequiredService<DailyCallBudget>();

        first.Should().BeSameAs(second);
    }
}
