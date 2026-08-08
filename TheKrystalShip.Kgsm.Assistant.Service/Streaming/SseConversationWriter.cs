using Microsoft.AspNetCore.Http.Features;

using TheKrystalShip.Kgsm.Assistant.Service.Security;
using TheKrystalShip.KGSM.Auth.Sessions;

namespace TheKrystalShip.Kgsm.Assistant.Service.Streaming;

/// <summary>
/// Holds one <c>GET /events</c> connection open and writes the caller's own conversation changes to it
/// as Server-Sent Events, so a chat open in two places agrees with itself without either side polling.
/// <para>
/// The stream carries no conversation content — it says a thing changed and, for the switches, where
/// they now stand. A transcript is re-read over the ordinary endpoint, which keeps one way of
/// obtaining history rather than a second, streaming one that could drift from it.
/// </para>
/// </summary>
internal static class SseConversationWriter
{
    /// <summary>
    /// How often the stream speaks when nothing has happened. It keeps the connection through a
    /// proxy's idle timeout, and it is the beat on which the caller's session is re-checked.
    /// </summary>
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(20);

    public static async Task WriteAsync(
        HttpContext http,
        IConversationEventBus bus,
        ISessionValidator sessions,
        AuthPrincipal principal)
    {
        var response = http.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        // Defeat reverse-proxy response buffering (e.g. nginx), or a frame sits in a buffer until the
        // next one pushes it out — which on a channel this quiet could be minutes.
        response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var ct = http.RequestAborted;
        using var subscription = bus.Subscribe(principal.UserId);

        try
        {
            // The stream names itself first. A surface sends this id back on the calls it makes, and the
            // events it caused then arrive stamped with it — which is how it tells its own echo from a
            // change made somewhere else, and declines to re-apply what it already did.
            await SseTurnWriter.WriteEventAsync(
                response, ConversationStream.Hello, new StreamHello(subscription.StreamId), ct);

            var next = subscription.Reader.ReadAsync(ct).AsTask();
            while (!ct.IsCancellationRequested)
            {
                var beat = Task.Delay(Heartbeat, ct);
                if (await Task.WhenAny(next, beat) == next)
                {
                    var ev = await next;
                    await SseTurnWriter.WriteEventAsync(response, ev.Name, ev.Payload, ct);
                    next = subscription.Reader.ReadAsync(ct).AsTask();
                    continue;
                }

                // Authority is checked for the life of the connection, not just at its open — a stream
                // held for an hour would otherwise outlive the logout that was meant to end it. The
                // relay path carries no session of its own, so there is nothing there to re-check.
                if (principal.SessionId.Length > 0 && !await sessions.IsValidAsync(principal.SessionId, ct))
                    break;

                // A comment frame: it keeps the connection alive and parses as nothing.
                await response.WriteAsync(": ping\n\n", ct);
                await response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // The client went away, or the host is shutting down. Either way there is nobody to tell.
        }
    }
}
