using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Envelope;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Kgsm.Assistant.Search;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant;

/// <summary>
/// The deterministic <c>search</c> composer: the operator's local indexed docs first,
/// the public web as a fallback. Pure routing over <see cref="IRetrieval"/> + <see cref="IWebSearch"/>
/// with NO nested model calls. The ladder:
/// <list type="number">
///   <item>a local hit at or above <see cref="SearchOptions.LocalMinScore"/> answers from the docs;</item>
///   <item>otherwise the web is tried, and any hits answer from the web;</item>
///   <item>otherwise a weak local hit (below the threshold) beats nothing — returned with a caveat;</item>
///   <item>otherwise, honestly empty — and a web <em>failure</em> is reported as "couldn't search",
///         never as "nothing exists" (the ecosystem's measured-or-unknown rule).</item>
/// </list>
/// Returns the shared <see cref="ToolResult{TData}"/> envelope: the model reads
/// <see cref="ToolResult{TData}.Summary"/> (the grounding text — unchanged), a surface reads the
/// <see cref="SearchData"/> card (the cited passages). Never throws (the ports don't either).
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

    public async Task<ToolResult<SearchData>> SearchAsync(
        string query, SearchScope scope = SearchScope.Auto, CancellationToken cancellationToken = default)
    {
        query = query.Trim();

        // 1. Local retrieval — skipped outright when the caller asked for the web. ⚠ Skipped rather
        //    than retrieved-and-ignored: a local hit is only ever a reason NOT to call the web, so
        //    fetching one here could only produce an answer nobody asked for. A disabled/unbuilt index
        //    returns a failed Result (never throws) — treated the same as "no local hits", so we
        //    degrade to the web rather than surfacing an error.
        RetrievedChunk[] localHits = [];
        if (scope != SearchScope.Web)
        {
            var local = await _retrieval.RetrieveAsync(query, cancellationToken);
            localHits = local.IsSuccess
                ? local.Value!.OrderByDescending(h => h.Score).ToArray()
                : [];
            if (local.IsFailure)
                _logger.LogDebug("Local retrieval unavailable for \"{Query}\": {Error}", query, local.Error);
        }

        // 2. A strong local hit answers from the docs — no web call (cheaper, and the operator's own
        //    docs are more trustworthy than a third-party snippet).
        if (localHits.Length > 0 && localHits[0].Score >= _options.LocalMinScore)
            return Envelope(query, SearchState.LocalStrong, FormatLocal(query, localHits, weak: false), LocalPassages(localHits));

        // 3. The web. ⚠ Not consulted when the caller confined the search to the documentation — an
        //    answer from somewhere they ruled out is worse than no answer.
        Result<IReadOnlyList<WebSearchHit>> web = scope == SearchScope.Local
            ? Result.Failure<IReadOnlyList<WebSearchHit>>("the search was limited to the local documentation")
            : await _webSearch.SearchAsync(query, cancellationToken);

        if (web.IsSuccess && web.Value!.Count > 0)
            return Envelope(query, SearchState.Web, FormatWeb(query, web.Value!), WebPassages(web.Value!));

        // 4. A weak local hit beats nothing when the web yielded nothing.
        if (localHits.Length > 0)
            return Envelope(query, SearchState.LocalWeak, FormatLocal(query, localHits, weak: true), LocalPassages(localHits));

        // 5. Nothing — and be honest about WHY: a web failure (transport, over budget, not configured)
        //    is not evidence that the thing doesn't exist. Neither empty case carries a card (nothing to cite).
        if (web.IsFailure)
        {
            _logger.LogInformation(
                "Search for \"{Query}\" found nothing locally and the web search failed: {Error}", query, web.Error);
            return Envelope(query, SearchState.SearchFailed,
                $"Couldn't search for \"{query}\" right now ({web.Error ?? "search unavailable"}), and the local " +
                "docs have nothing on it. Tell the user plainly that you couldn't look it up; do not retry.", []);
        }

        return Envelope(query, SearchState.Empty,
            $"No results for \"{query}\" in the operator's indexed docs or on the web.", []);
    }

    /// <summary>Wrap the grounding <paramref name="summary"/> (what the model reads — unchanged) and the
    /// cited <paramref name="passages"/> (what a surface renders) in the shared envelope. Confidence tracks
    /// the rung: a strong local hit is measured (<see cref="Confidence.Confirmed"/>), the web is a
    /// well-supported-but-possibly-stale inference (<see cref="Confidence.Likely"/>), everything else is
    /// <see cref="Confidence.Possible"/>.</summary>
    private static ToolResult<SearchData> Envelope(
        string query, SearchState state, string summary, IReadOnlyList<SearchPassage> passages) =>
        new(
            Tool: LlmTools.Search,
            Confidence: state switch
            {
                SearchState.LocalStrong => Confidence.Confirmed,
                SearchState.Web => Confidence.Likely,
                _ => Confidence.Possible,
            },
            Subject: new ResultRef(ResourceKind.Search, query),
            Summary: summary,
            Data: new SearchData(query, state, passages));

    /// <summary>The retrieved local chunks as cited passages (doc path + heading breadcrumb + score).</summary>
    private static IReadOnlyList<SearchPassage> LocalPassages(IReadOnlyList<RetrievedChunk> hits) =>
        hits.Select(h => new SearchPassage(
            SearchProvenance.Local, h.SourcePath,
            string.IsNullOrWhiteSpace(h.HeaderPath) ? null : h.HeaderPath, h.Text, h.Score)).ToArray();

    /// <summary>The web hits as cited passages (URL + page title + score).</summary>
    private static IReadOnlyList<SearchPassage> WebPassages(IReadOnlyList<WebSearchHit> hits) =>
        hits.Select(h => new SearchPassage(
            SearchProvenance.Web, h.Url,
            string.IsNullOrWhiteSpace(h.Title) ? null : h.Title, h.Content, h.Score)).ToArray();

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

        // ⚠ Said HERE, where the decision is actually made. This text is what the model reads at the
        // moment it has an answer in front of it and is deciding whether it will do — a rule stated
        // once in the system prompt, thousands of tokens earlier, competes with everything since.
        // Measured: given local passages that matched a game on topic and said nothing about the
        // question, the model concluded the search had failed and gave up rather than looking further.
        // Documentation cannot know about a patch released after it was written, and nothing else
        // tells the model that the web is still open to it.
        var next = weak
            ? "\nIf that doesn't answer the question, search the same words again with scope=\"web\" — "
              + "it is a different source and counts as a new search, not a repeat."
            : "\nThese are the operator's own docs and are authoritative about THIS host. They cannot "
              + "know about anything released or changed after they were written, so if the question "
              + "was about what is current — a version, a release date, recent news — search the same "
              + "words again with scope=\"web\". That is a different source, so it is a new search "
              + "rather than a repeat.";

        var footer = omitted > 0 ? $"\n({omitted} more passage(s) omitted to fit.)" : string.Empty;
        return $"{header}\n{sb}{next}{footer}";
    }

    /// <summary>Numbered grounding from web hits (title + snippet + URL) — external, possibly stale.</summary>
    private static string FormatWeb(string query, IReadOnlyList<WebSearchHit> hits)
    {
        var lines = hits.Select((h, i) => $"{i + 1}. {h.Title}\n   {h.Content}\n   source: {h.Url}");
        return $"Web results for \"{query}\" (external sources — cite the URLs, and note they may be out " +
               $"of date):\n{string.Join("\n", lines)}";
    }
}
