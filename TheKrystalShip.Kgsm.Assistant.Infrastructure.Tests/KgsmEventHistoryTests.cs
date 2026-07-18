using System.Net;
using System.Text;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using TheKrystalShip.Kgsm.Assistant.Audit;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Monitor;
using TheKrystalShip.Kgsm.Assistant.Ports;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Tests;

/// <summary>
/// Unit-tests the monitor event-history adapter's request/response mapping over a stubbed transport
/// — no real socket. Verifies it parses the monitor's <c>GET /events</c> shape (Phase B), builds the
/// right query string, and fails closed (never throws) on the unreachable / non-2xx / unparseable paths.
/// </summary>
public class KgsmEventHistoryTests
{
    /// <summary>A canned <see cref="HttpMessageHandler"/>: returns a fixed response and records the request.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public Uri? LastRequestUri { get; private set; }

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static KgsmEventHistory Create(StubHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        return new KgsmEventHistory(http, NullLogger<KgsmEventHistory>.Instance);
    }

    private const string TwoEventPayload = """
        {
          "count": 2,
          "nextCursorTs": null,
          "nextCursorId": null,
          "events": [
            { "id": "evt_a1b2", "ts": "2026-07-18T10:00:00Z", "type": "instance_started",
              "instance": "factorio-test", "actor": "discord:tester", "origin": "assistant", "data": {"instanceName":"factorio-test"} },
            { "id": "evt_c3d4", "ts": "2026-07-18T09:00:00Z", "type": "instance_crashed",
              "instance": "factorio-test", "actor": null, "origin": null, "data": {"exitCode":"139"} }
          ]
        }
        """;

    [Fact]
    public async Task GetEventsAsync_MapsMonitorJson_ToAuditEventRows()
    {
        var handler = new StubHandler(HttpStatusCode.OK, TwoEventPayload);

        var reading = await Create(handler).GetEventsAsync("factorio-test", sinceMs: null, limit: 200);

        reading.State.Should().Be(AuditReadState.Available);
        reading.Events.Should().HaveCount(2);
        reading.Events[0].Should().BeEquivalentTo(new
        {
            Id = "evt_a1b2",
            Type = "instance_started",
            Instance = "factorio-test",
            Actor = "discord:tester",
            Origin = "assistant",
        });
        // The second row's null actor/origin survive verbatim — never defaulted to a placeholder.
        reading.Events[1].Actor.Should().BeNull();
        reading.Events[1].Origin.Should().BeNull();
    }

    [Fact]
    public async Task GetEventsAsync_BuildsQueryString_WithInstanceAndSince()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"count":0,"nextCursorTs":null,"nextCursorId":null,"events":[]}""");

        await Create(handler).GetEventsAsync("minecraft", sinceMs: 1_752_000_000_000, limit: 50);

        handler.LastRequestUri.Should().NotBeNull();
        var query = handler.LastRequestUri!.Query;
        query.Should().Contain("instance=minecraft").And.Contain("since=1752000000000").And.Contain("limit=50");
    }

    [Fact]
    public async Task GetEventsAsync_NoInstance_OmitsTheInstanceParam()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"count":0,"nextCursorTs":null,"nextCursorId":null,"events":[]}""");

        await Create(handler).GetEventsAsync(null, sinceMs: null, limit: 200);

        handler.LastRequestUri!.Query.Should().NotContain("instance=");
    }

    [Fact]
    public async Task GetEventsAsync_EmptyPage_IsAvailable_NotUnavailable()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"count":0,"nextCursorTs":null,"nextCursorId":null,"events":[]}""");

        var reading = await Create(handler).GetEventsAsync("minecraft", null, 200);

        reading.State.Should().Be(AuditReadState.Available);
        reading.Events.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]        // event history disabled on that host
    [InlineData(HttpStatusCode.ServiceUnavailable)] // monitor's 503-until-first-tick
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task GetEventsAsync_NonSuccessStatus_MapsToMonitorUnavailable_NeverThrows(HttpStatusCode status)
    {
        var handler = new StubHandler(status, "");

        var reading = await Create(handler).GetEventsAsync("minecraft", null, 200);

        reading.State.Should().Be(AuditReadState.MonitorUnavailable);
        reading.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task GetEventsAsync_UnparseableBody_MapsToMonitorUnavailable_NeverThrows()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "not json");

        var reading = await Create(handler).GetEventsAsync("minecraft", null, 200);

        reading.State.Should().Be(AuditReadState.MonitorUnavailable);
    }

    [Fact]
    public async Task GetEventsAsync_UnreachableSocket_MapsToMonitorUnavailable_NeverThrows()
    {
        var http = new HttpClient(new ThrowingHandler()) { BaseAddress = new Uri("http://localhost") };
        var adapter = new KgsmEventHistory(http, NullLogger<KgsmEventHistory>.Instance);

        var reading = await adapter.GetEventsAsync("minecraft", null, 200);

        reading.State.Should().Be(AuditReadState.MonitorUnavailable);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }
}
