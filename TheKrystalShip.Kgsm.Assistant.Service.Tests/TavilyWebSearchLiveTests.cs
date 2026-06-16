using System.Net.Http.Headers;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Service.Configuration;
using TheKrystalShip.Kgsm.Assistant.Service.Search;

using Xunit.Abstractions;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

/// <summary>
/// Live smoke test against the REAL Tavily API, exercising the actual adapter code path
/// (request shape + JSON parse + Result mapping), with the Bearer header wired exactly as
/// Program.cs does. Spends one Tavily credit per run.
/// <para>
/// Gated: a no-op (asserts nothing) unless <c>WebSearch__ApiKey</c> is set — same convention as
/// the other live tests, so CI without a key stays green. Run with:
///   WebSearch__ApiKey=tvly-... dotnet test --filter FullyQualifiedName~TavilyWebSearchLiveTests
/// </para>
/// </summary>
public class TavilyWebSearchLiveTests
{
    private readonly ITestOutputHelper _output;
    public TavilyWebSearchLiveTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task LiveSearch_ReturnsHits_ThroughTheAdapter()
    {
        var apiKey = Environment.GetEnvironmentVariable("WebSearch__ApiKey");
        if (string.IsNullOrWhiteSpace(apiKey))
            return; // not configured → no-op (mirrors the env-gated KGSM live tests)

        var options = Options.Create(new WebSearchOptions
        {
            ApiKey = apiKey,
            MaxResults = 4,
            SearchDepth = "basic",
            MaxCallsPerDay = 1000,
        });

        // Mirror the Program.cs DI: BaseAddress + a default Bearer header (the adapter doesn't set it).
        using var http = new HttpClient { BaseAddress = new Uri("https://api.tavily.com/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var adapter = new TavilyWebSearch(
            http, options, new DailyCallBudget(options), NullLogger<TavilyWebSearch>.Instance);

        var result = await adapter.SearchAsync("Terraria latest version");

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Should().NotBeEmpty();
        result.Value!.Should().OnlyContain(h =>
            !string.IsNullOrWhiteSpace(h.Title) && !string.IsNullOrWhiteSpace(h.Url));

        foreach (var hit in result.Value!)
        {
            var snippet = hit.Content.Length <= 100 ? hit.Content : hit.Content[..100];
            _output.WriteLine($"- {hit.Title}  [{hit.Score:0.00}]  {hit.Url}\n    {snippet}");
        }
    }
}
