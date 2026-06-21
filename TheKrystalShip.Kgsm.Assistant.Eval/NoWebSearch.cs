using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Eval;

/// <summary>
/// A fail-closed <see cref="IWebSearch"/> for the MCQ eval. The with-RAG condition measures LOCAL
/// retrieval only — pulling in live web results would make the lift non-reproducible and conflate two
/// sources. With web disabled, the real <see cref="SearchAggregator"/> still runs its full local ladder
/// (strong hit → weak hit → honest empty), so the grounding the model sees is exactly the local path
/// production ships. Returns a failure (never throws), matching the port contract.
/// </summary>
internal sealed class NoWebSearch : IWebSearch
{
    public Task<Result<IReadOnlyList<WebSearchHit>>> SearchAsync(
        string query, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure<IReadOnlyList<WebSearchHit>>("web search is disabled in the MCQ eval"));
}
