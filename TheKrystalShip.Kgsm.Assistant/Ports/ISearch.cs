using TheKrystalShip.Kgsm.Assistant.Envelope;
using TheKrystalShip.Kgsm.Assistant.Search;

namespace TheKrystalShip.Kgsm.Assistant.Ports;

/// <summary>
/// The unified knowledge-search capability the model sees as the single <c>search</c> tool: the
/// operator's local indexed docs first, the public web as a fallback. A deterministic
/// composer over <see cref="IRetrieval"/> + <see cref="IWebSearch"/> with NO nested model calls.
/// Returns the shared <see cref="ToolResult{TData}"/> envelope: the model reads
/// <see cref="ToolResult{TData}.Summary"/> (ready-to-use grounding text, including honest
/// "nothing found" / "couldn't search" messages), a surface reads the <see cref="SearchData"/>
/// card (the cited passages). Never throws (the underlying ports don't either).
/// </summary>
public interface ISearch
{
    Task<ToolResult<SearchData>> SearchAsync(
        string query, SearchScope scope = SearchScope.Auto, CancellationToken cancellationToken = default);
}

/// <summary>
/// Where a search is allowed to look.
/// </summary>
/// <remarks>
/// <para>
/// <b>Local docs shadow the web, and that is right until somebody asks for the web.</b> The default
/// ladder answers from the operator's own documentation whenever it matches, which is cheaper and more
/// trustworthy — but a question about a game the docs cover retrieves those docs on topic alone,
/// however little they say about what was actually asked. Measured: "next Valheim update date" scored
/// a hit against fifty-four Valheim guide chunks and never reached the web, so somebody asking to
/// check online was answered from a local guide that says nothing about release dates.
/// </para>
/// <para>
/// <b>So where to look is the caller's to say.</b> Somebody who says "look online" has stated where
/// they want the answer from, and no similarity score is evidence against that.
/// </para>
/// </remarks>
public enum SearchScope
{
    /// <summary>Local documentation first, the web when it has nothing strong. The default.</summary>
    Auto,

    /// <summary>Only the operator's indexed documentation.</summary>
    Local,

    /// <summary>Only the public web — for current facts, and for anyone who asked to look online.</summary>
    Web,
}
