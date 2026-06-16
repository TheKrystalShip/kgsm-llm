using System.Net;
using System.Text;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Infrastructure.Configuration;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Search;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Tests;

/// <summary>
/// Unit-tests the Tavily adapter's request/response mapping over a stubbed transport — no
/// network. Verifies it maps the provider JSON to <c>WebSearchHit</c>s, sends the right
/// request, and fails closed (never throws) on the unconfigured / over-budget / non-2xx paths.
/// </summary>
public class TavilyWebSearchTests
{
    /// <summary>A canned <see cref="HttpMessageHandler"/>: returns a fixed response and records the request.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }
        public int Calls { get; private set; }

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static TavilyWebSearch Create(
        StubHandler handler, string apiKey = "tvly-test", int maxPerDay = 100)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.tavily.com/") };
        var options = Options.Create(new WebSearchOptions
        {
            ApiKey = apiKey,
            MaxResults = 4,
            SearchDepth = "basic",
            MaxCallsPerDay = maxPerDay,
        });
        var budget = new DailyCallBudget(options);
        return new TavilyWebSearch(http, options, budget, NullLogger<TavilyWebSearch>.Instance);
    }

    private const string TwoHitPayload = """
        {
          "query": "terraria latest version",
          "results": [
            { "title": "Terraria 1.4.5", "url": "https://terraria.org/news", "content": "1.4.5 is out.", "score": 0.98 },
            { "title": "Patch notes", "url": "https://terraria.org/patch", "content": "Bug fixes.", "score": 0.71 }
          ]
        }
        """;

    [Fact]
    public async Task MapsProviderJson_ToHits()
    {
        var handler = new StubHandler(HttpStatusCode.OK, TwoHitPayload);

        var result = await Create(handler).SearchAsync("terraria latest version");

        result.IsSuccess.Should().BeTrue();
        result.Value!.Should().HaveCount(2);
        result.Value![0].Should().BeEquivalentTo(
            new { Title = "Terraria 1.4.5", Url = "https://terraria.org/news", Content = "1.4.5 is out.", Score = 0.98 });
    }

    [Fact]
    public async Task SendsBearerAuth_ToSearchEndpoint_WithSnakeCaseBody()
    {
        var handler = new StubHandler(HttpStatusCode.OK, TwoHitPayload);

        await Create(handler).SearchAsync("terraria latest version");

        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest!.RequestUri!.AbsoluteUri.Should().Be("https://api.tavily.com/search");
        // Note: the Bearer header is set by DI in Program.cs, not the adapter, so it's not asserted
        // here; the body shape (snake_case, include_answer:false, basic depth) IS the adapter's job.
        handler.LastRequestBody.Should().Contain("\"search_depth\":\"basic\"")
            .And.Contain("\"include_answer\":false")
            .And.Contain("\"max_results\":4")
            .And.Contain("\"query\":\"terraria latest version\"");
    }

    [Fact]
    public async Task NonSuccessStatus_FailsClosed_NeverThrows()
    {
        var handler = new StubHandler(HttpStatusCode.TooManyRequests, "rate limited");

        var result = await Create(handler).SearchAsync("anything");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("429");
    }

    [Fact]
    public async Task EmptyApiKey_FailsClosed_WithoutCallingProvider()
    {
        var handler = new StubHandler(HttpStatusCode.OK, TwoHitPayload);

        var result = await Create(handler, apiKey: "").SearchAsync("anything");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("not configured");
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task DailyBudgetExhausted_FailsClosed_WithoutCallingProvider()
    {
        var handler = new StubHandler(HttpStatusCode.OK, TwoHitPayload);

        // Zero daily budget → the first call is refused before any HTTP request.
        var result = await Create(handler, maxPerDay: 0).SearchAsync("anything");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("daily web-search limit");
        handler.Calls.Should().Be(0);
    }
}
