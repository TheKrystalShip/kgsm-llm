using Microsoft.Extensions.Logging;

using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Kgsm.Assistant.Search;

namespace TheKrystalShip.Kgsm.Assistant.Blueprints;

/// <summary>
/// The deterministic <see cref="IBlueprintResearch"/> composer (plan step 2): <see cref="ISearch"/>
/// finds candidate pages, <see cref="IWebFetch"/> reads them, and <see cref="BlueprintFactExtractor"/>
/// pulls out sourced fields. Mirrors <see cref="SearchAggregator"/>'s shape — a pure routing composition
/// over existing ports, no nested model call — so it is independently testable with fakes for both
/// ports and honors the same budgets (fetch/search per-message caps live in the assistant gate, same as
/// every other <c>search</c>/<c>fetch_url</c> call). Never throws (neither underlying port does).
/// </summary>
public sealed class BlueprintResearchAggregator : IBlueprintResearch
{
    /// <summary>Pages fetched per research pass — small and deliberate: this is one automated pass, not
    /// an open-ended crawl, and each fetch counts against the same daily wallet as a model-driven
    /// fetch_url call.</summary>
    private const int MaxPagesToFetch = 3;

    private readonly ISearch _search;
    private readonly IWebFetch _webFetch;
    private readonly ILogger<BlueprintResearchAggregator> _logger;

    public BlueprintResearchAggregator(ISearch search, IWebFetch webFetch, ILogger<BlueprintResearchAggregator> logger)
    {
        _search = search;
        _webFetch = webFetch;
        _logger = logger;
    }

    public async Task<BlueprintResearchFindings> ResearchAsync(string game, CancellationToken cancellationToken = default)
    {
        game = game.Trim();
        var query = $"{game} dedicated server download self-host Linux";

        var searchResult = await _search.SearchAsync(query, cancellationToken);
        var webUrls = searchResult.Data.Passages
            .Where(p => p.Provenance == SearchProvenance.Web)
            .Select(p => p.Source)
            .Distinct()
            .Take(MaxPagesToFetch)
            .ToArray();

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
