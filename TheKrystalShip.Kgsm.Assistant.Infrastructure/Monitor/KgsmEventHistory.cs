using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

using TheKrystalShip.Kgsm.Assistant.Audit;
using TheKrystalShip.Kgsm.Assistant.Ports;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Monitor;

/// <summary>
/// Satisfies the assistant's <see cref="IEventHistory"/> port by reading the kgsm-monitor's
/// <c>GET /events</c> over its unix-domain socket (Phase B — the monitor is the single source of
/// truth for KGSM engine events; the assistant reads it directly, never via kgsm-api, plan §9).
/// Shares the exact transport shape <see cref="KgsmServerMetrics"/> uses for <c>GET /metrics</c> —
/// same socket, same <see cref="SocketsHttpHandler.ConnectCallback"/> pattern — because both scrape
/// the same daemon over the same <c>Monitor:SocketPath</c>.
/// <para>
/// Deliberately independent of the shared <c>Monitor.Contracts</c> NuGet: a small LOCAL DTO
/// deserializes only the fields the two tools need, so the assistant leaf keeps its "depends only on
/// kgsm-lib + a local Ollama" boundary (mirrors <see cref="KgsmServerMetrics"/>'s own rationale). Any
/// failure (connect refused, non-200 incl. the monitor's 503-until-first-tick / 404 when event
/// history is disabled, timeout, parse error) maps to <see cref="AuditReadState.MonitorUnavailable"/>.
/// Per the port contract this NEVER throws.
/// </para>
/// </summary>
internal sealed class KgsmEventHistory : IEventHistory
{
    // Deserialize the monitor's camelCase JSON; only the fields the audit tools need are modelled.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ILogger<KgsmEventHistory> _logger;

    public KgsmEventHistory(HttpClient http, ILogger<KgsmEventHistory> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<EventHistoryReading> GetEventsAsync(
        string? instance, long? sinceMs, int limit, CancellationToken cancellationToken = default)
    {
        MonitorEventsResponse? response;
        try
        {
            var url = "/events?limit=" + Uri.EscapeDataString(limit.ToString());
            if (!string.IsNullOrWhiteSpace(instance))
                url += "&instance=" + Uri.EscapeDataString(instance);
            if (sinceMs is not null)
                url += "&since=" + Uri.EscapeDataString(sinceMs.Value.ToString());

            using var httpResponse = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!httpResponse.IsSuccessStatusCode)
            {
                // 503 until the monitor's first tick, 404 when event history is disabled on that host,
                // any other non-2xx — all "no data right now", never narrated as "nothing happened".
                _logger.LogDebug("monitor /events returned {Status} for instance '{Instance}'",
                    (int)httpResponse.StatusCode, instance ?? "(all)");
                return Unavailable;
            }

            await using var stream = await httpResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            response = await JsonSerializer.DeserializeAsync<MonitorEventsResponse>(stream, Json, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or JsonException
                                      or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // Unreachable / slow / unparseable monitor is failure-as-data, never a thrown exception.
            _logger.LogDebug(ex, "monitor event-history scrape failed for instance '{Instance}'", instance ?? "(all)");
            return Unavailable;
        }

        if (response?.Events is null)
            return Unavailable;

        var rows = response.Events
            .Select(e => new AuditEventRow(e.Id, e.Ts, e.Type, e.Instance, e.Actor, e.Origin))
            .ToArray();

        return new EventHistoryReading(AuditReadState.Available, rows);
    }

    private static EventHistoryReading Unavailable =>
        new(AuditReadState.MonitorUnavailable, Array.Empty<AuditEventRow>());

    // --- Local DTO for the monitor's GET /events (only the fields the audit tools read) -------------
    // The monitor emits camelCase JSON, ts-DESC, Actor/Origin/Instance nullable exactly as the monitor's
    // honest "never fabricated" contract requires. Kept private and minimal so the assistant leaf takes
    // no dependency on the Monitor.Contracts package. `Data` is intentionally NOT modelled — neither
    // tool interprets the per-event payload, only the enrichment trio + type + timestamp.

    private sealed record MonitorEventsResponse(
        [property: JsonPropertyName("events")] List<MonitorEvent>? Events);

    private sealed record MonitorEvent(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("ts")] DateTimeOffset Ts,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("instance")] string? Instance,
        [property: JsonPropertyName("actor")] string? Actor,
        [property: JsonPropertyName("origin")] string? Origin);
}
