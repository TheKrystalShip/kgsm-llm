using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Ports;

namespace TheKrystalShip.Kgsm.Assistant;

/// <summary>
/// The deterministic <c>search</c> composer (plan §3.4): the operator's local indexed docs first,
/// the public web as a fallback. Pure routing over <see cref="IRetrieval"/> + <see cref="IWebSearch"/>
/// with NO nested model calls. The ladder:
/// <list type="number">
///   <item>a local hit at or above <see cref="SearchOptions.LocalMinScore"/> answers from the docs;</item>
///   <item>otherwise the web is tried, and any hits answer from the web;</item>
///   <item>otherwise a weak local hit (below the threshold) beats nothing — returned with a caveat;</item>
///   <item>otherwise, honestly empty — and a web <em>failure</em> is reported as "couldn't search",
///         never as "nothing exists" (the ecosystem's measured-or-unknown rule).</item>
/// </list>
/// Returns model-facing grounding text; never throws (the ports don't either).
/// </summary>
public sealed class SearchAggregator : ISearch
{
    private readonly IRetrieval _retrieval;
    private readonly IWebSearch _webSearch;
    private readonly SearchOptions _options;
    private readonly ILogger<SearchAggregator> _logger;

    public SearchAggregator(
        IRetrieval retrieval,
        IWebSearch webSearch,
        IOptions<SearchOptions> options,
        ILogger<SearchAggregator> logger)
    {
        _retrieval = retrieval;
        _webSearch = webSearch;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        query = query.Trim();

        // 1. Local retrieval. A disabled/unbuilt index returns a failed Result (never throws) — treated
        //    the same as "no local hits", so we degrade to the web rather than surfacing an error.
        var local = await _retrieval.RetrieveAsync(query, cancellationToken);
        var localHits = local.IsSuccess
            ? local.Value!.OrderByDescending(h => h.Score).ToArray()
            : [];
        if (local.IsFailure)
            _logger.LogDebug("Local retrieval unavailable for \"{Query}\": {Error}", query, local.Error);

        // 2. A strong local hit answers from the docs — no web call (cheaper, and the operator's own
        //    docs are more trustworthy than a third-party snippet).
        if (localHits.Length > 0 && localHits[0].Score >= _options.LocalMinScore)
            return FormatLocal(query, localHits, weak: false);

        // 3. Web fallback.
        var web = await _webSearch.SearchAsync(query, cancellationToken);
        if (web.IsSuccess && web.Value!.Count > 0)
            return FormatWeb(query, web.Value!);

        // 4. A weak local hit beats nothing when the web yielded nothing.
        if (localHits.Length > 0)
            return FormatLocal(query, localHits, weak: true);

        // 5. Nothing — and be honest about WHY: a web failure (transport, over budget, not configured)
        //    is not evidence that the thing doesn't exist.
        if (web.IsFailure)
        {
            _logger.LogInformation(
                "Search for \"{Query}\" found nothing locally and the web search failed: {Error}", query, web.Error);
            return $"Couldn't search for \"{query}\" right now ({web.Error ?? "search unavailable"}), and the local " +
                   "docs have nothing on it. Tell the user plainly that you couldn't look it up; do not retry.";
        }

        return $"No results for \"{query}\" in the operator's indexed docs or on the web.";
    }

    /// <summary>Numbered grounding from local chunks (heading breadcrumb + text + source path), capped at
    /// <see cref="SearchOptions.MaxContextChars"/> — the strongest chunk is always kept.</summary>
    private string FormatLocal(string query, IReadOnlyList<RetrievedChunk> hits, bool weak)
    {
        var sb = new StringBuilder();
        var used = 0;
        var omitted = 0;

        for (var i = 0; i < hits.Count; i++)
        {
            var h = hits[i];
            var label = string.IsNullOrWhiteSpace(h.HeaderPath) ? h.SourcePath : h.HeaderPath;
            var entry = $"{used + 1}. {label}\n   {h.Text}\n   source: {h.SourcePath}";

            // Always include the first (strongest) chunk; stop once the budget would be exceeded.
            if (used > 0 && sb.Length + entry.Length + 1 > _options.MaxContextChars)
            {
                omitted = hits.Count - i;
                break;
            }

            if (used > 0)
                sb.Append('\n');
            sb.Append(entry);
            used++;
        }

        var header = weak
            ? $"The local docs had no strong match for \"{query}\"; these are the closest passages and may " +
              "not directly answer it (say so if you rely on them):"
            : $"From the operator's indexed docs for \"{query}\" (local knowledge base — cite the source paths):";
        var footer = omitted > 0 ? $"\n({omitted} more passage(s) omitted to fit.)" : string.Empty;
        return $"{header}\n{sb}{footer}";
    }

    /// <summary>Numbered grounding from web hits (title + snippet + URL) — external, possibly stale.</summary>
    private static string FormatWeb(string query, IReadOnlyList<WebSearchHit> hits)
    {
        var lines = hits.Select((h, i) => $"{i + 1}. {h.Title}\n   {h.Content}\n   source: {h.Url}");
        return $"Web results for \"{query}\" (external sources — cite the URLs, and note they may be out " +
               $"of date):\n{string.Join("\n", lines)}";
    }
}
