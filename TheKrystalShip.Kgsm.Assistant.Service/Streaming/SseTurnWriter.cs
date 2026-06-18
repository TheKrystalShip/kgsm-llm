using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Http.Features;

using TheKrystalShip.Kgsm.Assistant;
using TheKrystalShip.Kgsm.Assistant.Service.Security;

namespace TheKrystalShip.Kgsm.Assistant.Service;

/// <summary>
/// Streams an assistant turn to the caller as Server-Sent Events. net9 has no built-in SSE result
/// (that arrives in net10), so we frame events onto the response body directly. The canonical §5·a
/// typed events (architecture.html §5·a / toolbox-plan §5·a / keystone O1): <c>text.delta</c> (reply
/// slices), <c>tool.start</c> / <c>tool.result</c> (per tool call, paired by a synthesised id),
/// <c>command.proposed</c> (a staged op in the §5·a shape, carrying the host-minted token),
/// <c>done</c> (the full reply), <c>error</c> (in-band failure), and the opt-in additive
/// <c>thinking.delta</c>. Every frame carries BOTH the SSE <c>event:</c> name and an in-band
/// <c>type</c> discriminator (same constant) so a client can key on either.
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

        // Per-turn monotonic id for command.proposed (the §5·a `id`; the SPA correlates
        // proposed→verified client-side — distinct from the security-bearing confirmation token).
        var proposalSeq = 0;

        try
        {
            await foreach (var ev in assistant
                               .RunStreamAsync(conversationId, prompt, canPerformActions, think, requestedTools, ct))
            {
                switch (ev.Kind)
                {
                    case AssistantEventKind.Token:
                        await WriteEventAsync(response, TurnStream.TextDelta, new TokenEvent(ev.Text ?? string.Empty), ct);
                        break;

                    case AssistantEventKind.Thinking:
                        await WriteEventAsync(response, TurnStream.ThinkingDelta, new ThinkingEvent(ev.Text ?? string.Empty), ct);
                        break;

                    case AssistantEventKind.ToolStart:
                        if (ev.ToolName is not null)
                            await WriteEventAsync(response, TurnStream.ToolStart,
                                new ToolStartEvent(
                                    ev.ToolCallId ?? string.Empty,
                                    ev.ToolName.Name,
                                    ev.ToolArguments ?? new Dictionary<string, string?>()),
                                ct);
                        break;

                    case AssistantEventKind.ToolResult:
                        if (ev.ToolName is not null)
                            await WriteEventAsync(response, TurnStream.ToolResult,
                                new ToolResultEvent(ev.ToolCallId ?? string.Empty, ev.ToolName.Name, ev.ToolSummary ?? string.Empty), ct);
                        break;

                    case AssistantEventKind.Confirmation:
                        // Mint the confirmation token HERE, bound to the verified caller — same as
                        // the buffered /turn path. The library only ever hands us the raw op. The
                        // token is RETAINED (additive) for the /confirm surfaces; the SPA routes a
                        // confirm to the API's M3 command path instead (fork (a)).
                        var c = ev.StagedConfirmation!;
                        var proposed = new CommandProposedEvent(
                            Id: $"cmd_{proposalSeq++}",
                            Verb: ApiVerb(c.Kind),
                            Subject: new CommandSubject(SubjectResource(c.Kind), c.Target),
                            Confirm: ComposeConfirm(c),
                            Token: tokens.Create(c, principal.UserId),
                            Reason: null,
                            ConfigKey: c.ConfigKey,
                            ConfigValue: c.ConfigValue);
                        await WriteEventAsync(response, TurnStream.CommandProposed, proposed, ct);
                        break;

                    case AssistantEventKind.Error:
                        await WriteEventAsync(response, TurnStream.Error,
                            new StreamErrorEvent("assistant_failed", ev.ErrorMessage ?? "The assistant failed."), ct);
                        break;

                    case AssistantEventKind.Final:
                        await WriteEventAsync(response, TurnStream.Done,
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
        // Emit BOTH the SSE `event:` name and an in-band `type` discriminator (same value), so a
        // client can key on either (§5·a frames the events with `type`; the SSE `event:` line is
        // the EventSource-native form). Inject `type` into the serialized object rather than adding
        // a field to every DTO — one source of truth for the name, DTOs stay clean.
        var node = JsonSerializer.SerializeToNode(payload, Json)!.AsObject();
        node["type"] = eventName;
        await response.WriteAsync($"event: {eventName}\ndata: {node.ToJsonString(Json)}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }

    /// <summary>The normalised §5·a API verb token for a staged kind (the SPA routes a confirm to
    /// the M3 command path by it). Distinct from <see cref="ConfirmationKinds.Verb"/>'s human label.</summary>
    private static string ApiVerb(ConfirmationKind kind) => kind switch
    {
        ConfirmationKind.Start => "start",
        ConfirmationKind.Stop => "stop",
        ConfirmationKind.Restart => "restart",
        ConfirmationKind.Update => "update",
        ConfirmationKind.Install => "install",
        ConfirmationKind.Uninstall => "uninstall",
        ConfirmationKind.Backup => "backup",
        ConfirmationKind.SetConfig => "set_config",
        _ => kind.ToString().ToLowerInvariant(),
    };

    /// <summary>The §5·a subject resource for a staged kind: Install targets a blueprint, the rest an instance.</summary>
    private static string SubjectResource(ConfirmationKind kind) =>
        kind == ConfirmationKind.Install ? "blueprint" : "server";

    /// <summary>A human-readable confirm prompt composed from the staged op (never fabricated detail).</summary>
    private static string ComposeConfirm(PendingConfirmation c)
    {
        if (c.Kind == ConfirmationKind.SetConfig && c.ConfigKey is not null)
            return $"Set {c.ConfigKey} = {c.ConfigValue} on {c.Target}?";

        var verb = ConfirmationKinds.Verb(c.Kind); // "start", "back up", "set config on", …
        return $"{char.ToUpperInvariant(verb[0])}{verb[1..]} {c.Target}?";
    }
}
