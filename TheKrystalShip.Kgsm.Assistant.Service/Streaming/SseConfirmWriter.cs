using System.Text.Json;
using System.Threading.Channels;

using Microsoft.AspNetCore.Http.Features;

using TheKrystalShip.Kgsm.Assistant;
using TheKrystalShip.Kgsm.Assistant.Blueprints;
using TheKrystalShip.Kgsm.Assistant.Envelope;
using TheKrystalShip.Kgsm.Assistant.Service.PendingWrites;
using TheKrystalShip.Kgsm.Assistant.Service.Security;

namespace TheKrystalShip.Kgsm.Assistant.Service;

/// <summary>
/// Streams a blueprint FINALIZE (<c>/confirm</c> for a <see cref="ConfirmationKind.Blueprint"/> token) to
/// the caller as Server-Sent Events. A finalize runs the test-install → boot → verify → bounded-repair
/// pipeline, which is <em>minutes</em> of work that emits <strong>no output for long stretches</strong>
/// (a SteamCMD download, a boot-log poll). Delivered as a single buffered HTTP response, that is a
/// multi-minute silence on one socket — which an idle-connection reaper anywhere on a remote path
/// (NAT, a middlebox, the browser) will drop, leaving the chat's "verifying" card spinning forever with
/// no terminal result. Streaming fixes both halves of that:
/// <list type="bullet">
/// <item>the pipeline's own <see cref="ITurnProgress"/> steps (research / install / verify / repair) are
/// relayed as <c>progress</c> frames, so the user sees it advancing instead of a dead spinner;</item>
/// <item>a heartbeat comment every <see cref="HeartbeatSeconds"/>s keeps bytes flowing through the silent
/// stretches, so no idle reaper fires;</item>
/// <item>a terminal <c>result</c> frame carries the whole <see cref="ConfirmResponse"/> — the SAME payload
/// a buffered caller gets — so the card ALWAYS reaches a terminal state.</item>
/// </list>
/// The finalize runs as a hot task with the progress sink flowing into it via <see cref="ITurnProgress"/>'s
/// <see cref="AsyncLocal{T}"/> (set by <see cref="ITurnProgress.BeginTurn"/> BEFORE the task starts), exactly
/// as the streaming <c>/turn</c> path does. Token validation, authority, and the edited-YAML resolution all
/// happen in the endpoint BEFORE this writer is called, so a bad token is still a clean pre-stream 4xx —
/// once the first frame flushes the status is committed and any failure is the in-band <c>error</c> event.
/// </summary>
internal static class SseConfirmWriter
{
    private const int HeartbeatSeconds = 15;

    public static async Task WriteAsync(
        HttpContext http,
        IServerAssistant assistant,
        ITurnProgress progress,
        ConfirmationTokenService tokens,
        IPendingWriteStore pendingWrites,
        int confirmationTtlSeconds,
        AuthPrincipal principal,
        string game,
        string editedYaml,
        bool canPerformActions)
    {
        var response = http.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        // Defeat reverse-proxy response buffering so frames (and the keep-alive heartbeats) reach the SPA promptly.
        response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var ct = http.RequestAborted;

        // The finalize reports its steps onto this channel via the ambient ITurnProgress sink. Unbounded +
        // single-reader (this drain loop) + potentially multiple writers deep in the pipeline — the same
        // shape the streaming turn uses.
        var channel = Channel.CreateUnbounded<AssistantStreamEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        // Start the finalize. BeginTurn sets the AsyncLocal sink BEFORE the task's synchronous prologue runs,
        // so every ITurnProgress.Report deep inside the aggregator lands on `channel`. The task completes the
        // writer in its finally, which ends the drain loop below.
        var finalizeTask = RunFinalizeAsync(
            assistant, progress, channel.Writer, tokens, pendingWrites, confirmationTtlSeconds,
            principal, game, editedYaml, canPerformActions, ct);

        try
        {
            await DrainWithHeartbeatAsync(response, channel.Reader, ct);
            var confirm = await finalizeTask; // completed once the writer was completed in RunFinalizeAsync's finally
            await SseTurnWriter.WriteEventAsync(response, TurnStream.Result, confirm, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client disconnected mid-finalize. The finalize task shares `ct`, so it is unwinding too; there is
            // nothing left to write to a gone socket. Observe the task so its cancellation isn't unhandled.
            try { await finalizeTask; } catch { /* already cancelled/faulted — nothing to surface to a gone client */ }
        }
        catch (Exception ex)
        {
            // The status is already committed (200 + progress frames), so we can't change it — surface the
            // failure in-band as the terminal `error` event, mirroring the turn stream's contract.
            try { await finalizeTask; } catch { /* fold into the error below */ }
            await SseTurnWriter.WriteEventAsync(
                response, TurnStream.Error, new StreamErrorEvent("finalize_failed", ex.Message), ct);
        }
    }

    /// <summary>
    /// Relays progress steps from <paramref name="reader"/> as <c>progress</c> frames, interleaving a
    /// heartbeat comment whenever no step has arrived for <see cref="HeartbeatSeconds"/>s. Returns when the
    /// channel is completed (the finalize finished). The pending read is persisted across heartbeat ticks so
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
                        break; // channel completed → finalize done
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
                    // keep the socket warm through a long silent install/verify stretch.
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
    /// Runs <see cref="IServerAssistant.FinalizeBlueprintAsync"/> under the ambient progress scope and builds
    /// the <see cref="ConfirmResponse"/> the terminal frame carries — the SAME shape the buffered <c>/confirm</c>
    /// path returns (a Verified card, or a fresh DraftReady card + re-edit token when the repair loop exhausts).
    /// Completes the channel writer in a finally so the drain loop always ends, whatever the outcome.
    /// </summary>
    private static async Task<ConfirmResponse> RunFinalizeAsync(
        IServerAssistant assistant,
        ITurnProgress progress,
        ChannelWriter<AssistantStreamEvent> writer,
        ConfirmationTokenService tokens,
        IPendingWriteStore pendingWrites,
        int confirmationTtlSeconds,
        AuthPrincipal principal,
        string game,
        string editedYaml,
        bool canPerformActions,
        CancellationToken ct)
    {
        using var progressScope = progress.BeginTurn(writer);
        try
        {
            var outcome = await assistant.FinalizeBlueprintAsync(game, editedYaml, canPerformActions, ct);
            var data = outcome.Data;

            // On DraftReady (repair exhausted / invalid edit), re-stage the returned draft under a fresh
            // Blueprint token so the user can edit + save again — identical to the buffered path.
            IReadOnlyList<ConfirmationDto>? reEdit = null;
            if (data is not null && data.Outcome == BlueprintAuthoringOutcome.DraftReady && data.DraftYaml is not null)
            {
                var restaged = PendingWriteTokenSwap.ForToken(
                    new PendingConfirmation(ConfirmationKind.Blueprint, data.BlueprintName ?? game,
                        InstanceName: game, ConfigValue: data.DraftYaml),
                    pendingWrites, confirmationTtlSeconds);
                reEdit =
                [
                    new ConfirmationDto(
                        restaged.Kind.ToString().ToLowerInvariant(), restaged.Target, restaged.InstanceName,
                        tokens.Create(restaged, principal.UserId), restaged.ConfigKey, restaged.ConfigValue),
                ];
            }

            var card = JsonSerializer.SerializeToElement(ToolResultCard.From(outcome), SseTurnWriter.Json);
            var verified = data?.Outcome == BlueprintAuthoringOutcome.Verified;
            return new ConfirmResponse(outcome.Summary, verified, card, reEdit);
        }
        finally
        {
            writer.Complete();
        }
    }
}
