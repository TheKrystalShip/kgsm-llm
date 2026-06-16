using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Configuration;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Search;

/// <summary>
/// Tavily-backed <see cref="IWebSearch"/>. A typed <see cref="HttpClient"/> (factory-owned,
/// transient) with the API key set as a default Bearer header in the DI registration. Never
/// throws — every failure (unconfigured, over-budget, transport, bad JSON, non-2xx) is a
/// <see cref="Result.Failure{T}(string)"/>.
/// <para>
/// Guards: the per-message cap lives in the assistant gate (<c>ServerAssistant.BuildGate</c>);
/// the per-day wallet cap is <see cref="DailyCallBudget"/>, checked here. We request
/// <c>search_depth=basic</c> (1 credit) and <c>include_answer=false</c> because the model does
/// the synthesis — Tavily's own answer would be a redundant spend.
/// </para>
/// </summary>
internal sealed class TavilyWebSearch : IWebSearch
{
    private readonly HttpClient _http;
    private readonly WebSearchOptions _options;
    private readonly DailyCallBudget _budget;
    private readonly ILogger<TavilyWebSearch> _logger;

    public TavilyWebSearch(
        HttpClient http,
        IOptions<WebSearchOptions> options,
        DailyCallBudget budget,
        ILogger<TavilyWebSearch> logger)
    {
        _http = http;
        _options = options.Value;
        _budget = budget;
        _logger = logger;
    }

    public async Task<Result<IReadOnlyList<WebSearchHit>>> SearchAsync(
        string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            return Result.Failure<IReadOnlyList<WebSearchHit>>("web search is not configured");

        if (!_budget.TryConsume())
            return Result.Failure<IReadOnlyList<WebSearchHit>>("the daily web-search limit has been reached");

        // Snake_case on the wire; the API key travels as a default Bearer header set in DI.
        var body = new
        {
            query,
            search_depth = _options.SearchDepth,
            max_results = _options.MaxResults,
            include_answer = false,
        };

        try
        {
            using var response = await _http.PostAsJsonAsync("search", body, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Tavily search returned {Status}", (int)response.StatusCode);
                return Result.Failure<IReadOnlyList<WebSearchHit>>(
                    $"the search provider returned {(int)response.StatusCode}");
            }

            var payload = await response.Content.ReadFromJsonAsync<TavilyResponse>(cancellationToken);
            var hits = (payload?.Results ?? new List<TavilyHit>())
                .Select(r => new WebSearchHit(
                    r.Title ?? string.Empty,
                    r.Url ?? string.Empty,
                    r.Content ?? string.Empty,
                    r.Score))
                .ToArray();

            return Result.Success<IReadOnlyList<WebSearchHit>>(hits);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // TaskCanceledException covers the HttpClient timeout (a slow search must not stall
            // the agent loop). A caller-cancelled token also lands here — fine, same graceful message.
            _logger.LogWarning(ex, "Tavily search failed for query '{Query}'", query);
            return Result.Failure<IReadOnlyList<WebSearchHit>>("the web search could not be completed");
        }
    }

    private sealed record TavilyResponse(
        [property: JsonPropertyName("results")] List<TavilyHit>? Results);

    private sealed record TavilyHit(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("content")] string? Content,
        [property: JsonPropertyName("score")] double Score);
}
