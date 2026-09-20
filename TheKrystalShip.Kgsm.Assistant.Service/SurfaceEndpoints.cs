using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

using TheKrystalShip.Api.Contracts;
using TheKrystalShip.KGSM.ComponentSurface;

namespace TheKrystalShip.Kgsm.Assistant.Service;

/// <summary>
/// What this service answers about <b>itself</b>: its configuration, its unit and its journal.
/// </summary>
/// <remarks>
/// <para>
/// A component owns these wherever it runs. What differs is the way a browser reaches them — a leaf is
/// reached through the node that runs it, an anchor at its own address — and this service is whichever
/// its deployment makes it, so it serves them here and both standings read the same surface.
/// </para>
/// <para>
/// Everything below the transport is <see cref="ComponentConfigService"/>,
/// <see cref="ComponentUnitReader"/> and <see cref="ComponentJournal"/>: the one implementation of the
/// descriptor's rules, in the repo that also generates the file they read.
/// </para>
/// <para>
/// Admin, on the same group as the conversation review and for a stronger reason: these values name
/// where the conversation store and the signing key live, and the journal carries usernames, addresses
/// and the shape of every failure this service has had.
/// </para>
/// </remarks>
internal static class SurfaceEndpoints
{
    /// <summary>The heartbeat that keeps an idle journal from reading as a dropped connection.</summary>
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(20);

    internal static void Map(RouteGroupBuilder admin)
    {
        // What this service can be configured with, and what it is running on.
        admin.MapGet("/config", (ComponentConfigService config) =>
            config.Read() is { } view
                ? Results.Ok(view)
                : Results.NotFound(new ErrorEnvelope(new ErrorBody("no_descriptor",
                    "This service has no config descriptor installed, so it describes no configuration surface."))));

        // Set or reset keys, then restart to pick them up. The restart is queued before the answer is
        // written and the answer still arrives, because systemd stops the unit with SIGTERM and the host
        // drains what is in flight — which is what lets the answer say whether the job was accepted.
        admin.MapPut("/config", (ComponentConfigUpdate? body, ComponentConfigService config) =>
        {
            if (body is null)
            {
                return Results.BadRequest(new ErrorEnvelope(new ErrorBody("malformed_request",
                    "The request body is not readable.")));
            }

            (ComponentApplyOutcome? outcome, string? error) = config.Apply(body);

            if (error is not null)
                return Results.BadRequest(new ErrorEnvelope(new ErrorBody("invalid_value", error)));

            return outcome is null
                ? Results.NotFound(new ErrorEnvelope(new ErrorBody("no_descriptor",
                    "This service has no config descriptor installed, so there is nothing to configure.")))
                : Results.Ok(outcome.Result);
        });

        // What systemd reports about this service's unit — the row the panel's System tab renders.
        admin.MapGet("/system", async (ComponentUnitReader units, CancellationToken ct) =>
            await units.ReadAsync(ct).ConfigureAwait(false) is { } row
                ? Results.Ok(row)
                : Results.NotFound(new ErrorEnvelope(new ErrorBody("no_descriptor",
                    "This service has no config descriptor installed, so it names no unit to report on."))));

        // The scrollback. A journal that cannot be read is a different fact from one that has nothing in
        // it, and it is reported as one rather than as an empty page.
        admin.MapGet("/logs", (ComponentJournal journal, int? lines) =>
            journal.Read(lines) is { } read
                ? Results.Ok(new LogPage(read, null))
                : Results.Json(new ErrorEnvelope(new ErrorBody("journal_unreadable",
                    "This service's journal could not be read on this host.")),
                    statusCode: StatusCodes.Status503ServiceUnavailable));

        admin.MapGet("/logs/stream", StreamAsync);
    }

    /// <summary>
    /// <c>GET /admin/logs/stream</c> — the same lines, as they happen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Server-sent events rather than a socket, because this carries one thing in one direction and a
    /// browser reconnects it for free. It is read with <c>fetch</c> on the panel's side for the ordinary
    /// reason: <c>EventSource</c> sends no <c>Authorization</c> header, and this journal is not going
    /// behind a token in a query string.
    /// </para>
    /// <para>
    /// <b>Follow-only.</b> The caller hydrated its scrollback from the read above and applies lines from
    /// the next one on, so nothing here replays history — sending it would show every line twice on
    /// every attach.
    /// </para>
    /// <para>
    /// The comment line at the start is what makes a proxy release the response: a stream that has
    /// carried no bytes is one several of them hold on to until it does.
    /// </para>
    /// </remarks>
    private static async Task StreamAsync(HttpContext ctx, ComponentJournalFollower follower)
    {
        if (follower.Watch() is not { } watch)
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await ctx.Response.WriteAsJsonAsync(new ErrorEnvelope(new ErrorBody("journal_unreadable",
                "This service's journal could not be followed on this host.")), ctx.RequestAborted);
            return;
        }

        using IDisposable handle = watch.Handle;

        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
        ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        await ctx.Response.WriteAsync(": open\n\n", ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

        var heartbeat = new PeriodicTimer(Heartbeat);
        Task<bool> tick = heartbeat.WaitForNextTickAsync(ctx.RequestAborted).AsTask();

        try
        {
            while (!ctx.RequestAborted.IsCancellationRequested)
            {
                Task<bool> lines = watch.Lines.WaitToReadAsync(ctx.RequestAborted).AsTask();
                Task done = await Task.WhenAny(lines, tick).ConfigureAwait(false);

                if (done == tick)
                {
                    if (!await tick)
                        break;
                    tick = heartbeat.WaitForNextTickAsync(ctx.RequestAborted).AsTask();
                    await ctx.Response.WriteAsync(": ping\n\n", ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                    continue;
                }

                // The follow ended. Closing is the honest answer: the panel says the tail stopped rather
                // than showing a live pill over a stream carrying nothing.
                if (!await lines)
                    break;

                while (watch.Lines.TryRead(out LogLine? line))
                {
                    string json = JsonSerializer.Serialize(line, ApiContractsJson.Default.LogLine);
                    await ctx.Response.WriteAsync("data: " + json + "\n\n", ctx.RequestAborted);
                }

                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            }
        }
        catch (OperationCanceledException)
        {
            // The browser left. Nothing to report: the handle's disposal is what matters, and it is what
            // stops the follow when this was the last watcher.
        }
        finally
        {
            heartbeat.Dispose();
        }
    }
}
