using TheKrystalShip.Kgsm.Assistant.Envelope;

namespace TheKrystalShip.Kgsm.Assistant.Search;

/// <summary>Where a search passage came from — the operator's local indexed docs, or the public web.</summary>
public enum SearchProvenance
{
    /// <summary>A chunk from the operator's local RAG index.</summary>
    Local,

    /// <summary>A hit from the public web (Tavily).</summary>
    Web,
}

/// <summary>
/// Which rung of the <see cref="SearchAggregator"/> ladder answered. The three hit states
/// (<see cref="LocalStrong"/> / <see cref="LocalWeak"/> / <see cref="Web"/>) carry citations and
/// surface as a card; <see cref="Empty"/> and <see cref="SearchFailed"/> carry no passages and stay
/// summary-only (the dispatcher attaches a card only when there is something to cite).
/// </summary>
public enum SearchState
{
    /// <summary>A local hit at or above the score threshold answered from the docs.</summary>
    LocalStrong,

    /// <summary>Only weak local hits (below threshold) — the closest passages, offered with a caveat.</summary>
    LocalWeak,

    /// <summary>The web answered (no strong local hit).</summary>
    Web,

    /// <summary>Nothing found in the docs or on the web (an honest empty result).</summary>
    Empty,

    /// <summary>The web search failed (transport / over budget / not configured) — not evidence of absence.</summary>
    SearchFailed,
}

/// <summary>
/// One cited passage on a search card: its provenance, the source reference (a doc path for local,
/// a URL for web), an optional title (heading breadcrumb / page title), the snippet, and the
/// retrieval score. The surface renders this as a citation; the model reads the same content from
/// the tool's <see cref="ToolResult{TData}.Summary"/>.
/// </summary>
public sealed record SearchPassage(
    SearchProvenance Provenance,
    string Source,
    string? Title,
    string Text,
    double Score);

/// <summary>
/// The <c>search</c> tool's structured card payload (the surface half of its <see cref="ToolResult{TData}"/>):
/// the query, which rung answered, and the cited passages that grounded the answer. Built only when
/// the search found something to cite — an empty / "couldn't search" outcome is summary-only.
/// </summary>
public sealed record SearchData(
    string Query,
    SearchState State,
    IReadOnlyList<SearchPassage> Passages);
