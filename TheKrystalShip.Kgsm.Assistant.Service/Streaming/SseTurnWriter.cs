using System.Text.Json;

using Microsoft.AspNetCore.Http.Features;

using TheKrystalShip.Kgsm.Assistant;
using TheKrystalShip.Kgsm.Assistant.Service.Security;

namespace TheKrystalShip.Kgsm.Assistant.Service;

/// <summary>
/// Streams an assistant turn to the caller as Server-Sent Events. net9 has no built-in SSE result
/// (that arrives in net10), so we frame events onto the response body directly. The canonical §5a
/// typed events (toolbox-plan §5a / keystone O1): <c>text.delta</c> (reply slices),
/// <c>tool.start</c> / <c>tool.result</c> (per tool call), <c>command.proposed</c> (a staged op
/// plus its host-minted token), <c>done</c> (the full reply), and <c>error</c> (in-band failure).
/// <para>
/// The session bearer is already enforced by <see cref="BearerAuthFilter"/> before we get here.
/// The response commits HTTP 200 the moment the first frame flushes, so any failure after that is
/// the in-band <c>error</c> event — never a status code.
/// </para>
/// </summary>
internal static class SseTurnWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task WriteAsync(
        HttpContext http,
        IServerAssistant assistant,
        ConfirmationTokenService tokens,
        AuthPrincipal principal,
        string conversationId,
        string prompt,
        bool canPerformActions,
        bool think = false,
        IReadOnlyList<string>? requestedTools = null)
    {
        var response = http.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        // Defeat reverse-proxy response buffering (e.g. nginx) so frames reach the SPA promptly.
        response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        // RequestAborted so a disconnected SPA aborts the underlying Ollama generation — the box's
        // single GPU is deliberately reserved away from the game servers, so an abandoned stream
        // that kept generating would be a real cost.
        var ct = http.RequestAborted;

        try
        {
            await foreach (var ev in assistant
                               .RunStreamAsync(conversationId, prompt, canPerformActions, think, requestedTools, ct))
            {
                switch (ev.Kind)
                {
                    case AssistantEventKind.Token:
                        await WriteEventAsync(response, "text.delta", new TokenEvent(ev.Text ?? string.Empty), ct);
                        break;

                    case AssistantEventKind.Thinking:
                        await WriteEventAsync(response, "thinking.delta", new ThinkingEvent(ev.Text ?? string.Empty), ct);
                        break;

                    case AssistantEventKind.ToolStart:
                        if (ev.ToolName is not null)
                            await WriteEventAsync(response, "tool.start",
                                new ToolStartEvent(
                                    ev.ToolName.Name,
                                    ev.ToolArguments ?? new Dictionary<string, string?>()),
                                ct);
                        break;

                    case AssistantEventKind.ToolResult:
                        if (ev.ToolName is not null)
                            await WriteEventAsync(response, "tool.result",
                                new ToolResultEvent(ev.ToolName.Name, ev.ToolSummary ?? string.Empty), ct);
                        break;

                    case AssistantEventKind.Confirmation:
                        // Mint the confirmation token HERE, bound to the verified caller — same as
                        // the buffered /turn path. The library only ever hands us the raw op.
                        var c = ev.StagedConfirmation!;
                        var dto = new ConfirmationDto(
                            c.Kind.ToString().ToLowerInvariant(), c.Target, c.InstanceName,
                            tokens.Create(c, principal.UserId), c.ConfigKey, c.ConfigValue);
                        await WriteEventAsync(response, "command.proposed", dto, ct);
                        break;

                    case AssistantEventKind.Error:
                        await WriteEventAsync(response, "error",
                            new StreamErrorEvent(ev.ErrorMessage ?? "The assistant failed."), ct);
                        break;

                    case AssistantEventKind.Final:
                        await WriteEventAsync(response, "done",
                            new DoneEvent(ev.Text ?? string.Empty, UsageDto.From(ev.Usage)), ct);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client disconnected mid-stream; disposing the enumerator already aborted generation.
            // There's nothing left to write — stop quietly.
        }
    }

    private static async Task WriteEventAsync<T>(
        HttpResponse response, string eventName, T payload, CancellationToken ct)
    {
        var data = JsonSerializer.Serialize(payload, Json);
        await response.WriteAsync($"event: {eventName}\ndata: {data}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }
}
