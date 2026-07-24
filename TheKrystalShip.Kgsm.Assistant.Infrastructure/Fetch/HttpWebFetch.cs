using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Infrastructure.Configuration;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Fetch;

/// <summary>
/// The <see cref="IWebFetch"/> adapter: a direct HTTP GET of ONE caller-given URL, made safe for an
/// internet-exposed host taking a model/user-influenced address. A typed <see cref="HttpClient"/>
/// (factory-owned, transient) whose primary handler disables auto-redirect and resolves every
/// connection through <see cref="SsrfGuard.ResolveSafeAsync"/> (registered in
/// <c>ServiceCollectionExtensions.AddKgsmAdapters</c>) — this class only implements the
/// scheme/policy/budget checks and the redirect loop; the address-safety guarantee itself lives in
/// <see cref="SsrfGuard"/>. Never throws — every failure (unconfigured, blocked, over budget, bad
/// URL, transport, non-2xx, unsupported content-type, too many redirects) is a
/// <see cref="Result.Failure{T}(string)"/>.
/// </summary>
internal sealed class HttpWebFetch : IWebFetch
{
    private readonly HttpClient _http;
    private readonly WebFetchOptions _options;
    private readonly DailyFetchBudget _budget;
    private readonly ILogger<HttpWebFetch> _logger;

    public HttpWebFetch(HttpClient http, IOptions<WebFetchOptions> options, DailyFetchBudget budget, ILogger<HttpWebFetch> logger)
    {
        _http = http;
        _options = options.Value;
        _budget = budget;
        _logger = logger;
    }

    public async Task<Result<WebFetchResult>> FetchAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            return Result.Failure<WebFetchResult>("URL fetching is not configured");

        if (!_budget.TryConsume())
            return Result.Failure<WebFetchResult>("the daily URL-fetch limit has been reached");

        if (!TryParseHttpUrl(url, out var current, out var parseError))
            return Result.Failure<WebFetchResult>(parseError!);

        for (var hop = 0; ; hop++)
        {
            if (hop > _options.MaxRedirects)
                return Result.Failure<WebFetchResult>($"too many redirects (over {_options.MaxRedirects})");

            if (HostPolicy.IsBlocked(current.Host, _options.AllowedHosts, _options.DeniedHosts, out var policyError))
                return Result.Failure<WebFetchResult>(policyError!);

            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                // SocketsHttpHandler wraps a ConnectCallback throw (an SsrfBlockedException) in an
                // HttpRequestException — unwrap it so a blocked host reads as "refused", not a generic
                // transport failure.
                if (ex.InnerException is SsrfBlockedException blocked)
                {
                    _logger.LogWarning("Refused to fetch {Url}: {Reason}", current, blocked.Message);
                    return Result.Failure<WebFetchResult>($"refused to fetch this address ({blocked.Message})");
                }

                _logger.LogWarning(ex, "Fetch failed for {Url}", current);
                return Result.Failure<WebFetchResult>("the page could not be reached");
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // The HttpClient.Timeout firing (a caller-cancelled token also throws this, but is
                // covered by the ambient cancellation and propagates normally instead of being caught).
                _logger.LogWarning(ex, "Fetch timed out for {Url}", current);
                return Result.Failure<WebFetchResult>("the fetch timed out");
            }

            using (response)
            {
                if (IsRedirect(response.StatusCode))
                {
                    var location = response.Headers.Location;
                    if (location is null)
                        return Result.Failure<WebFetchResult>($"the server returned {(int)response.StatusCode} with no redirect target");

                    var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (next.Scheme != Uri.UriSchemeHttp && next.Scheme != Uri.UriSchemeHttps)
                        return Result.Failure<WebFetchResult>($"redirected to an unsupported scheme ('{next.Scheme}')");

                    current = next;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    return Result.Failure<WebFetchResult>($"the server returned {(int)response.StatusCode}");

                var contentType = response.Content.Headers.ContentType?.MediaType;
                if (!ContentClassifier.IsTextual(contentType))
                    return Result.Failure<WebFetchResult>(
                        contentType is null
                            ? "the page has no content-type header and was not fetched"
                            : $"content type '{contentType}' is not text and was skipped");

                var (raw, truncated) = await ReadCappedAsync(response.Content, _options.MaxContentBytes, cancellationToken);
                var isHtml = string.Equals(contentType, "text/html", StringComparison.OrdinalIgnoreCase);
                var text = isHtml ? HtmlTextExtractor.ExtractText(raw) : raw;
                var title = isHtml ? HtmlTextExtractor.ExtractTitle(raw) : null;

                return Result.Success(new WebFetchResult(current.ToString(), (int)response.StatusCode, contentType, title, text, truncated));
            }
        }
    }

    /// <summary>Only <c>http</c>/<c>https</c>, absolute URLs are accepted — checked BEFORE any
    /// network call, so <c>file:</c>/<c>ftp:</c>/<c>gopher:</c>/etc. never reach the transport.</summary>
    private static bool TryParseHttpUrl(string url, out Uri uri, out string? error)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri!))
        {
            error = "not a valid absolute URL";
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            error = $"unsupported URL scheme '{uri.Scheme}' (only http/https are allowed)";
            return false;
        }

        error = null;
        return true;
    }

    private static bool IsRedirect(System.Net.HttpStatusCode status) => status is
        System.Net.HttpStatusCode.MovedPermanently or
        System.Net.HttpStatusCode.Found or
        System.Net.HttpStatusCode.SeeOther or
        System.Net.HttpStatusCode.TemporaryRedirect or
        System.Net.HttpStatusCode.PermanentRedirect;

    /// <summary>Streams the body into memory up to <paramref name="capBytes"/>, stopping (and
    /// reporting <c>Truncated</c>) rather than buffering an unbounded page. Decoded leniently as
    /// UTF-8 (invalid sequences become U+FFFD) — good enough for LLM-readable text without a
    /// dependency on sniffing the real declared charset.</summary>
    private static async Task<(string Text, bool Truncated)> ReadCappedAsync(
        HttpContent content, int capBytes, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        var truncated = false;

        int read;
        while ((read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken)) > 0)
        {
            var remaining = capBytes - (int)buffer.Length;
            if (remaining <= 0)
            {
                truncated = true;
                break;
            }

            var toWrite = Math.Min(remaining, read);
            buffer.Write(chunk, 0, toWrite);
            if (toWrite < read)
            {
                truncated = true;
                break;
            }
        }

        return (System.Text.Encoding.UTF8.GetString(buffer.ToArray()), truncated);
    }
}
