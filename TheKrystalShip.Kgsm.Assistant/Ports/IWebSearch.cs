using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Ports;

/// <summary>
/// One external web result: pre-extracted, LLM-ready text plus its source URL. Not a KGSM
/// resource — it comes from an external search provider, so the dispatcher treats it as
/// external grounding (cite-able, possibly stale), never a measured fact.
/// </summary>
public sealed record WebSearchHit(string Title, string Url, string Content, double Score);

/// <summary>
/// External web search — a leaf capability for looking up facts that are NOT about this host's
/// servers (e.g. a game's latest version, what a config key does). Deliberately NOT routed
/// through kgsm-lib: that chokepoint is for the kgsm engine only, and web search is an external
/// concern, so it stays local to the assistant. The host supplies the provider adapter (Tavily
/// today). Implementations MUST NOT throw — return a failed <see cref="Result"/> instead.
/// </summary>
public interface IWebSearch
{
    Task<Result<IReadOnlyList<WebSearchHit>>> SearchAsync(
        string query, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IWebSearch"/> for hosts that have not wired a provider: every search fails
/// closed with a clear reason, so embedding the assistant library never breaks DI just because
/// web search isn't configured. <c>AddKgsmAssistant</c> registers this with <c>TryAddSingleton</c>;
/// a host that wants real search registers a concrete adapter afterward (e.g. via
/// <c>AddHttpClient&lt;IWebSearch, TavilyWebSearch&gt;</c>) — that later registration is the one
/// resolved.
/// </summary>
internal sealed class DisabledWebSearch : IWebSearch
{
    public Task<Result<IReadOnlyList<WebSearchHit>>> SearchAsync(
        string query, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure<IReadOnlyList<WebSearchHit>>("web search is not configured on this host"));
}
