using System.Threading.Channels;

using Microsoft.AspNetCore.Http.Features;

namespace TheKrystalShip.Kgsm.Assistant.Service;

/// <summary>
/// Streams a <c>/confirm</c> to the caller as Server-Sent Events. A confirmed operation can be a
/// long time answering and emit <strong>no output for long stretches</strong>: a blueprint finalize
/// runs a minutes-long test-install → boot → verify → bounded-repair pipeline, an install downloads
/// a game, and a lifecycle command is watched until it reaches its run state. Delivered as a single
/// buffered HTTP response that is a long silence on one socket — which an idle-connection reaper
/// anywhere on a remote path (NAT, a middlebox, the browser) will drop, leaving the caller's card
/// spinning forever with no terminal result. Streaming fixes both halves of that:
/// <list type="bullet">
/// <item>steps reported through <see cref="ITurnProgress"/> are relayed as <c>progress</c> frames,
/// so the user sees it advancing instead of a dead spinner;</item>
/// <item>a heartbeat comment every <see cref="HeartbeatSeconds"/>s keeps bytes flowing through the
/// silent stretches, so no idle reaper fires;</item>
/// <item>a terminal <c>result</c> frame carries the whole <see cref="ConfirmResponse"/> — the SAME
/// payload a buffered caller gets — so the card ALWAYS reaches a terminal state.</item>
/// </list>
/// <para>
/// The work itself is supplied by the caller, so this type owns only the
/// streaming mechanics and every confirmation kind reaches the wire through one path. It runs as a
/// hot task with the progress sink flowing into it via <see cref="ITurnProgress"/>'s
/// <see cref="AsyncLocal{T}"/> (set by <see cref="ITurnProgress.BeginTurn"/> before the first
/// await), exactly as the streaming <c>/turn</c> path does.
/// </para>
/// <para>
/// Token validation, authority, and any payload rehydration all happen in the endpoint BEFORE this
/// writer is called, so a bad token is still a clean pre-stream 4xx — once the first frame flushes
/// the status is committed and any failure is the in-band <c>error</c> event.
/// </para>
/// </summary>
internal static class SseConfirmWriter
{
    private const int HeartbeatSeconds = 15;

    public static async Task WriteAsync(
        HttpContext http,
        ITurnProgress progress,
        Func<CancellationToken, Task<ConfirmResponse>> run)
    {
        var response = http.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        // Defeat reverse-proxy response buffering so frames (and the keep-alive heartbeats) reach the SPA promptly.
        response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var ct = http.RequestAborted;

        // The work reports its steps onto this channel via the ambient ITurnProgress sink. Unbounded +
        // single-reader (this drain loop) + potentially multiple writers deep in the pipeline — the same
        // shape the streaming turn uses.
        var channel = Channel.CreateUnbounded<AssistantStreamEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        // Start the work. BeginTurn sets the AsyncLocal sink BEFORE the task's synchronous prologue runs,
        // so every ITurnProgress.Report deep inside it lands on `channel`. The task completes the writer
        // in its finally, which ends the drain loop below.
        var workTask = RunAsync(progress, channel.Writer, run, ct);

        try
        {
            await DrainWithHeartbeatAsync(response, channel.Reader, ct);
            var confirm = await workTask; // completed once the writer was completed in RunAsync's finally
            await SseTurnWriter.WriteEventAsync(response, TurnStream.Result, confirm, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client disconnected mid-run. The work shares `ct`, so it is unwinding too; there is nothing left
            // to write to a gone socket. Observe the task so its cancellation isn't unhandled.
            try { await workTask; } catch { /* already cancelled/faulted — nothing to surface to a gone client */ }
        }
        catch (Exception ex)
        {
            // The status is already committed (200 + progress frames), so we can't change it — surface the
            // failure in-band as the terminal `error` event, mirroring the turn stream's contract.
            try { await workTask; } catch { /* fold into the error below */ }
            await SseTurnWriter.WriteEventAsync(
                response, TurnStream.Error, new StreamErrorEvent("confirm_failed", ex.Message), ct);
        }
    }

    /// <summary>
    /// Relays progress steps from <paramref name="reader"/> as <c>progress</c> frames, interleaving a
    /// heartbeat comment whenever no step has arrived for <see cref="HeartbeatSeconds"/>s. Returns when the
    /// channel is completed (the work finished). The pending read is persisted across heartbeat ticks so
    /// it is never issued twice concurrently on the single-reader channel.
    /// </summary>
    private static async Task DrainWithHeartbeatAsync(
        HttpResponse response, ChannelReader<AssistantStreamEvent> reader, CancellationToken ct)
    {
        var heartbeat = TimeSpan.FromSeconds(HeartbeatSeconds);
        var enumerator = reader.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        try
        {
            Task<bool>? moveNext = null;
            while (true)
            {
                moveNext ??= enumerator.MoveNextAsync().AsTask();
                var winner = await Task.WhenAny(moveNext, Task.Delay(heartbeat, ct));
                if (winner == moveNext)
                {
                    var hasNext = await moveNext;
                    moveNext = null;
                    if (!hasNext)
                        break; // channel completed → work done
                    var ev = enumerator.Current;
                    if (ev.Kind == AssistantEventKind.Progress)
                        await SseTurnWriter.WriteEventAsync(response, TurnStream.Progress,
                            new ProgressEvent(
                                ev.ToolName?.Name ?? string.Empty,
                                ev.ProgressKey ?? string.Empty,
                                ev.ProgressLabel ?? string.Empty,
                                ev.ProgressStatus ?? "active",
                                ev.ToolCallId),
                            ct);
                }
                else
                {
                    // Keep-alive comment — no `data:`, so the SPA's SSE parser ignores it for free, but the bytes
                    // keep the socket warm through a long silent install/verify/settle stretch.
                    await response.WriteAsync(": keepalive\n\n", ct);
                    await response.Body.FlushAsync(ct);
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    /// <summary>
    /// Runs the supplied work under the ambient progress scope, completing the channel writer in a finally
    /// so the drain loop always ends, whatever the outcome.
    /// </summary>
    private static async Task<ConfirmResponse> RunAsync(
        ITurnProgress progress,
        ChannelWriter<AssistantStreamEvent> writer,
        Func<CancellationToken, Task<ConfirmResponse>> run,
        CancellationToken ct)
    {
        using var progressScope = progress.BeginTurn(writer);
        try
        {
            return await run(ct);
        }
        finally
        {
            writer.Complete();
        }
    }
}
