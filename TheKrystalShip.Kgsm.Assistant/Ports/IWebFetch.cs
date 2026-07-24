using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Ports;

/// <summary>
/// One successfully fetched page: the URL actually reached (after any redirects), the HTTP status,
/// the response content-type, an optional page title (HTML only — cheap to extract from the same
/// parse, never fabricated for non-HTML content), the extracted text, and whether the adapter's size
/// cap clipped the body. A FAILED fetch never produces one of these — see <see cref="IWebFetch"/>.
/// </summary>
public sealed record WebFetchResult(
    string FinalUrl,
    int StatusCode,
    string? ContentType,
    string? Title,
    string Text,
    bool Truncated);

/// <summary>
/// Fetches ONE specific web page and returns its extracted text — a leaf capability distinct from
/// <see cref="IWebSearch"/>: search FINDS pages via a provider's summarized hits, this READS a page
/// the caller already has (or just found) the URL for. Deliberately NOT routed through kgsm-lib (same
/// reasoning as <see cref="IWebSearch"/>): this is an external concern, not an engine one.
/// <para>
/// The URL is model/user-influenced and this host is internet-exposed, so the concrete adapter carries
/// real safety weight (scheme allowlist, an SSRF guard re-validated on every redirect hop, a size cap,
/// a timeout, and content-type filtering) — see the adapter's own doc comments. This port only declares
/// the contract; it makes no safety guarantee itself.
/// </para>
/// Implementations MUST NOT throw — a failure (unconfigured, blocked, over budget, transport, non-2xx,
/// unsupported content-type) is a <see cref="Result.Failure{T}(string)"/>, never an exception and never
/// a successful result with empty text (the measured-or-unknown / honesty rule: a fetch FAILURE must
/// read as "couldn't fetch", not as "the page is empty").
/// </summary>
public interface IWebFetch
{
    Task<Result<WebFetchResult>> FetchAsync(string url, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IWebFetch"/> for hosts that have not wired an adapter: every fetch fails closed
/// with a clear reason, so embedding the assistant library never breaks DI just because URL fetching
/// isn't configured. <c>AddKgsmAssistant</c> registers this with <c>TryAddSingleton</c>; a host that
/// wants real fetching registers a concrete adapter afterward (e.g. via
/// <c>AddHttpClient&lt;IWebFetch, HttpWebFetch&gt;</c>) — that later registration is the one resolved.
/// </summary>
internal sealed class DisabledWebFetch : IWebFetch
{
    public Task<Result<WebFetchResult>> FetchAsync(string url, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure<WebFetchResult>("URL fetching is not configured on this host"));
}
