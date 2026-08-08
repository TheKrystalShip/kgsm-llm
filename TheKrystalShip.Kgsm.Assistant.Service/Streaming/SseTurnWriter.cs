using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Http.Features;

using TheKrystalShip.Kgsm.Assistant.Service.Security;

namespace TheKrystalShip.Kgsm.Assistant.Service;

using TheKrystalShip.Kgsm.Assistant.Service.Streaming;

/// <summary>
/// Streams one turn to the caller of <c>POST /turn</c> as Server-Sent Events. The turn itself runs in a
/// <see cref="TurnSession"/> with its own lifetime, so what happens here is an <b>attach</b>: this
/// response is the session's first consumer, and it receives exactly the frames every other attached
/// surface receives.
/// <para>
/// The canonical §5·a typed events: <c>text.delta</c> (reply slices), <c>tool.start</c> /
/// <c>tool.result</c> (per tool call, paired by a synthesised id), <c>command.proposed</c> (a staged op
/// carrying the host-minted handle), <c>done</c> (the full reply), <c>error</c> (in-band failure), the
/// opt-in additive <c>thinking.delta</c>, and the additive <c>progress</c>. Every frame carries BOTH the
/// SSE <c>event:</c> name and an in-band <c>type</c> discriminator (same constant) so a client can key
/// on either. <c>turn.attach</c> precedes them all, stating everything that happened before this
/// consumer arrived.
/// </para>
/// <para>
/// The session bearer is already enforced by <see cref="BearerAuthFilter"/> before we get here. The
/// response commits HTTP 200 the moment the first frame flushes, so any failure after that is the
/// in-band <c>error</c> event — never a status code. A caller going away no longer ends the turn: the
/// session decides that, from whether anyone is still present.
/// </para>
/// </summary>
internal static class SseTurnWriter
{
    // Web defaults (camelCase, case-insensitive) + enums AS camelCase strings so the §5·a
    // tool.result card's enums (Confidence/CheckState/Severity/ResourceKind) render as
    // "warn"/"pass"/"success" — never opaque integers — for the SPA.
    // Also reused by the /confirm blueprint-finalize response so its card serializes identically to the
    // tool.result card here (enums as camelCase strings, boxed card Data by runtime type).
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Prepare the response for a stream of frames.</summary>
    internal static void OpenStream(HttpContext http)
    {
        var response = http.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        // Defeat reverse-proxy response buffering (e.g. nginx) so frames reach the SPA promptly.
        response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
    }

    /// <summary>
    /// Attach this response to <paramref name="session"/> and write its frames until the turn ends or
    /// the caller goes away. Leaving does not stop the turn — it detaches, and the session runs on for
    /// whoever else is watching.
    /// </summary>
    public static async Task AttachAsync(
        HttpContext http, TurnSession session, ITurnRegistry registry)
    {
        OpenStream(http);
        var response = http.Response;
        var ct = http.RequestAborted;

        var (consumer, attach) = session.Attach(registry.Queued(session.ConversationId));
        try
        {
            await WriteEventAsync(response, ConversationStream.TurnAttach, attach, ct);

            await foreach (var frame in consumer.Reader.ReadAllAsync(ct))
            {
                // A consumer that fell behind is redrawn rather than fed deltas with a hole in them:
                // its backlog is dropped and the session restates itself.
                if (consumer.NeedsRedraw)
                {
                    await WriteEventAsync(
                        response, ConversationStream.TurnAttach,
                        session.Redraw(consumer, registry.Queued(session.ConversationId)), ct);
                    continue;
                }
                await WriteEventAsync(response, frame.Name, frame.Payload, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // The caller went away mid-turn. Nothing left to write here; the turn is not ours to end.
        }
        finally
        {
            session.Detach(consumer);
        }
    }

    internal static async Task WriteEventAsync<T>(
        HttpResponse response, string eventName, T payload, CancellationToken ct)
    {
        // Emit BOTH the SSE `event:` name and an in-band `type` discriminator (same value), so a
        // client can key on either (§5·a frames the events with `type`; the SSE `event:` line is
        // the EventSource-native form). Inject `type` into the serialized object rather than adding
        // a field to every DTO — one source of truth for the name, DTOs stay clean.
        var node = JsonSerializer.SerializeToNode(payload, Json)!.AsObject();
        node["type"] = eventName;
        await response.WriteAsync($"event: {eventName}\ndata: {node.ToJsonString(Json)}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }
}
