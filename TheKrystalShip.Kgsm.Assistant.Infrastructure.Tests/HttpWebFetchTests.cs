using System.Net;
using System.Text;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Infrastructure.Configuration;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Fetch;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Tests;

/// <summary>
/// Unit-tests <see cref="HttpWebFetch"/>'s own logic (scheme/URL parsing, budget/enabled gating,
/// content-type classification, the manual redirect loop, the size cap) over a stubbed transport — NO
/// real socket, NO real SSRF guard (that layer only exists on the production <c>SocketsHttpHandler</c>
/// registered by <c>AddKgsmAdapters</c>; it's proven separately by <c>SsrfConnectCallbackTests</c>).
/// Mirrors <c>TavilyWebSearchTests</c>' <c>StubHandler</c> pattern.
/// </summary>
public class HttpWebFetchTests
{
    /// <summary>A scripted <see cref="HttpMessageHandler"/>: returns queued responses in order (or a
    /// fixed one if only one was given) and records every request URI.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses;
        public List<Uri> RequestedUris { get; } = [];
        public int Calls => RequestedUris.Count;

        public ScriptedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) =>
            _responses = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(responses);

        public ScriptedHandler(params HttpResponseMessage[] responses)
            : this(responses.Select(r => (Func<HttpRequestMessage, HttpResponseMessage>)(_ => r)).ToArray())
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUris.Add(request.RequestUri!);
            var next = _responses.Count > 1 ? _responses.Dequeue() : _responses.Peek();
            return Task.FromResult(next(request));
        }
    }

    /// <summary>A handler that blocks past the caller's HttpClient timeout, so
    /// <see cref="HttpClient.Timeout"/> fires a real (not simulated) cancellation.</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static HttpResponseMessage TextResponse(HttpStatusCode status, string body, string contentType = "text/plain")
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return response;
    }

    private static HttpResponseMessage Redirect(HttpStatusCode status, string location)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private static HttpWebFetch Create(
        HttpMessageHandler handler, WebFetchOptions? options = null, TimeSpan? timeout = null)
    {
        var opts = options ?? new WebFetchOptions { Enabled = true, MaxCallsPerDay = 100 };
        var http = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(10) };
        return new HttpWebFetch(http, Options.Create(opts), new DailyFetchBudget(Options.Create(opts)), NullLogger<HttpWebFetch>.Instance);
    }

    // --- gating: no network call at all -------------------------------------------------------------

    [Theory]
    [InlineData("ftp://example.com/file")]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://example.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not a url")]
    public async Task RejectsNonHttpOrMalformedUrl_WithoutAnyNetworkCall(string url)
    {
        var handler = new ScriptedHandler(TextResponse(HttpStatusCode.OK, "unused"));

        var result = await Create(handler).FetchAsync(url);

        result.IsSuccess.Should().BeFalse();
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Disabled_FailsClosed_WithoutAnyNetworkCall()
    {
        var handler = new ScriptedHandler(TextResponse(HttpStatusCode.OK, "unused"));

        var result = await Create(handler, new WebFetchOptions { Enabled = false }).FetchAsync("https://example.com/");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("not configured");
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task DailyBudgetExhausted_FailsClosed_WithoutAnyNetworkCall()
    {
        var handler = new ScriptedHandler(TextResponse(HttpStatusCode.OK, "unused"));

        var result = await Create(handler, new WebFetchOptions { Enabled = true, MaxCallsPerDay = 0 }).FetchAsync("https://example.com/");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("daily URL-fetch limit");
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Denylisted_FailsClosed_WithoutAnyNetworkCall()
    {
        var handler = new ScriptedHandler(TextResponse(HttpStatusCode.OK, "unused"));
        var options = new WebFetchOptions { Enabled = true, MaxCallsPerDay = 100, DeniedHosts = ["example.com"] };

        var result = await Create(handler, options).FetchAsync("https://example.com/");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("denylist");
        handler.Calls.Should().Be(0);
    }

    // --- content handling ----------------------------------------------------------------------------

    [Fact]
    public async Task HtmlContentType_ExtractsTextAndTitle()
    {
        const string html = "<html><head><title>Setup Guide</title><script>evil()</script></head>" +
                             "<body><p>Run the server binary.</p></body></html>";
        var handler = new ScriptedHandler(TextResponse(HttpStatusCode.OK, html, "text/html"));

        var result = await Create(handler).FetchAsync("https://docs.example.com/setup");

        result.IsSuccess.Should().BeTrue();
        result.Value!.Title.Should().Be("Setup Guide");
        result.Value!.Text.Should().Contain("Run the server binary.");
        result.Value!.Text.Should().NotContain("evil(");
        result.Value!.ContentType.Should().Be("text/html");
    }

    [Fact]
    public async Task PlainTextContentType_PassesThroughVerbatim()
    {
        const string dockerfile = "FROM debian:bookworm-slim\nRUN apt-get update\nCMD [\"/bin/server\"]\n";
        var handler = new ScriptedHandler(TextResponse(HttpStatusCode.OK, dockerfile, "text/plain"));

        var result = await Create(handler).FetchAsync("https://raw.example.com/Dockerfile");

        result.IsSuccess.Should().BeTrue();
        result.Value!.Text.Should().Be(dockerfile);
        result.Value!.Title.Should().BeNull();
    }

    [Fact]
    public async Task BinaryContentType_IsRefusedHonestly()
    {
        var handler = new ScriptedHandler(TextResponse(HttpStatusCode.OK, "PNG...", "image/png"));

        var result = await Create(handler).FetchAsync("https://example.com/logo.png");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("image/png");
    }

    [Fact]
    public async Task MissingContentType_IsRefusedHonestly()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        response.Content.Headers.ContentType = null;
        var handler = new ScriptedHandler(response);

        var result = await Create(handler).FetchAsync("https://example.com/mystery");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("content-type");
    }

    [Fact]
    public async Task NonSuccessStatus_FailsClosed()
    {
        var handler = new ScriptedHandler(TextResponse(HttpStatusCode.NotFound, "nope"));

        var result = await Create(handler).FetchAsync("https://example.com/missing");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("404");
    }

    // --- size cap --------------------------------------------------------------------------------

    [Fact]
    public async Task ExceedingSizeCap_SetsTruncated()
    {
        var body = new string('a', 10_000);
        var handler = new ScriptedHandler(TextResponse(HttpStatusCode.OK, body, "text/plain"));
        var options = new WebFetchOptions { Enabled = true, MaxCallsPerDay = 100, MaxContentBytes = 100 };

        var result = await Create(handler, options).FetchAsync("https://example.com/big");

        result.IsSuccess.Should().BeTrue();
        result.Value!.Truncated.Should().BeTrue();
        result.Value!.Text.Length.Should().Be(100);
    }

    [Fact]
    public async Task UnderSizeCap_IsNotTruncated()
    {
        var handler = new ScriptedHandler(TextResponse(HttpStatusCode.OK, "short body", "text/plain"));
        var options = new WebFetchOptions { Enabled = true, MaxCallsPerDay = 100, MaxContentBytes = 1000 };

        var result = await Create(handler, options).FetchAsync("https://example.com/small");

        result.Value!.Truncated.Should().BeFalse();
    }

    // --- redirects -------------------------------------------------------------------------------

    [Fact]
    public async Task Redirect_IsFollowedAndFinalUrlReflectsTheLastHop()
    {
        var handler = new ScriptedHandler(
            _ => Redirect(HttpStatusCode.Found, "https://example.com/final"),
            _ => TextResponse(HttpStatusCode.OK, "landed", "text/plain"));

        var result = await Create(handler).FetchAsync("https://example.com/start");

        result.IsSuccess.Should().BeTrue();
        result.Value!.FinalUrl.Should().Be("https://example.com/final");
        result.Value!.Text.Should().Be("landed");
        handler.Calls.Should().Be(2, "one request per redirect hop over the SAME handler — the property " +
            "SsrfConnectCallbackTests relies on to show every hop is re-validated in production");
    }

    [Fact]
    public async Task RedirectToNonHttpScheme_IsRejected()
    {
        var handler = new ScriptedHandler(_ => Redirect(HttpStatusCode.Found, "file:///etc/passwd"));

        var result = await Create(handler).FetchAsync("https://example.com/start");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("scheme");
        handler.Calls.Should().Be(1, "the second (file://) hop must never be requested");
    }

    [Fact]
    public async Task TooManyRedirects_FailsClosed()
    {
        var handler = new ScriptedHandler(_ => Redirect(HttpStatusCode.Found, "https://example.com/next"));
        var options = new WebFetchOptions { Enabled = true, MaxCallsPerDay = 100, MaxRedirects = 2 };

        var result = await Create(handler, options).FetchAsync("https://example.com/start");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("redirects");
    }

    // --- timeout ---------------------------------------------------------------------------------

    [Fact]
    public async Task Timeout_SurfacesACleanOutcome()
    {
        var result = await Create(new HangingHandler(), timeout: TimeSpan.FromMilliseconds(100))
            .FetchAsync("https://example.com/slow");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("timed out");
    }
}
