using Microsoft.Extensions.Logging;

using TheKrystalShip.Kgsm.Assistant.Ports;

namespace TheKrystalShip.Kgsm.Assistant.Blueprints;

/// <summary>
/// The deterministic <see cref="IBlueprintResearch"/> composer (plan step 2): <see cref="IWebSearch"/>
/// finds candidate pages, <see cref="IWebFetch"/> reads them, and <see cref="BlueprintFactExtractor"/>
/// pulls out sourced fields. A pure routing composition over existing ports — no nested model call — so
/// it is independently testable with fakes for both ports and honors the same budgets (fetch/search
/// per-message caps live in the assistant gate, same as every other <c>search</c>/<c>fetch_url</c> call).
/// Never throws (neither underlying port does).
/// <para>
/// It reaches <see cref="IWebSearch"/> (the raw web provider) directly rather than the local-index-first
/// <c>ISearch</c> aggregator: blueprint research needs fetchable third-party pages — an official server
/// doc, a Steam page, a community setup guide — and those live on the public web, never in this host's
/// own documentation index. A local-first search answers a "how do I host X" query from KGSM's docs and
/// never reaches the web, leaving no page to fetch.
/// </para>
/// </summary>
public sealed class BlueprintResearchAggregator : IBlueprintResearch
{
    /// <summary>Pages fetched per research pass — small and deliberate: this is one automated pass, not
    /// an open-ended crawl, and each fetch counts against the same daily wallet as a model-driven
    /// fetch_url call.</summary>
    private const int MaxPagesToFetch = 3;

    private readonly IWebSearch _webSearch;
    private readonly IWebFetch _webFetch;
    private readonly ILogger<BlueprintResearchAggregator> _logger;

    public BlueprintResearchAggregator(IWebSearch webSearch, IWebFetch webFetch, ILogger<BlueprintResearchAggregator> logger)
    {
        _webSearch = webSearch;
        _webFetch = webFetch;
        _logger = logger;
    }

    public async Task<BlueprintResearchFindings> ResearchAsync(string game, CancellationToken cancellationToken = default)
    {
        game = game.Trim();
        var query = $"{game} dedicated server download self-host Linux";

        var search = await _webSearch.SearchAsync(query, cancellationToken);
        var webUrls = search.IsSuccess && search.Value is not null
            ? search.Value
                .Select(h => h.Url)
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Distinct()
                .Take(MaxPagesToFetch)
                .ToArray()
            : [];

        if (webUrls.Length == 0)
        {
            _logger.LogInformation("Blueprint research for \"{Game}\" found no web sources to fetch", game);
            return new BlueprintResearchFindings(
                BlueprintFeasibility.Inconclusive, null, [], [],
                $"No web sources were found for \"{game}\" — nothing to research from.");
        }

        var pages = new List<(string Url, string Text)>();
        foreach (var url in webUrls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fetch = await _webFetch.FetchAsync(url, cancellationToken);
            if (fetch.IsSuccess)
                pages.Add((fetch.Value!.FinalUrl, fetch.Value!.Text));
            else
                _logger.LogInformation("Blueprint research for \"{Game}\" could not fetch {Url}: {Error}", game, url, fetch.Error);
        }

        if (pages.Count == 0)
            return new BlueprintResearchFindings(
                BlueprintFeasibility.Inconclusive, null, [], webUrls,
                $"Found {webUrls.Length} candidate source(s) for \"{game}\" but none could be fetched.");

        return BlueprintFactExtractor.Extract(game, pages);
    }
}
