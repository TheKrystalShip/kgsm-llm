using TheKrystalShip.Kgsm.Assistant.Service.PendingConfirmations;
using TheKrystalShip.Kgsm.Assistant.Service.Security;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Service.Streaming;

/// <summary>
/// Turns one agent event into the wire frame that represents it. Called once per event, by the session
/// that owns the run — so every consumer of that turn is handed the very same frame rather than each
/// re-deriving one, which is what makes two surfaces watching a turn agree by construction.
/// </summary>
internal static class TurnFrames
{
    public static TurnFrame? From(
        AssistantStreamEvent ev,
        AuthPrincipal principal,
        IPendingConfirmationStore pending,
        int confirmationTtlSeconds,
        ref int proposalSeq) => ev.Kind switch
        {
            AssistantEventKind.Token =>
                new TurnFrame(TurnStream.TextDelta, new TokenEvent(ev.Text ?? string.Empty)),

            AssistantEventKind.Thinking =>
                new TurnFrame(TurnStream.ThinkingDelta, new ThinkingEvent(ev.Text ?? string.Empty)),

            AssistantEventKind.ToolStart when ev.ToolName is not null =>
                new TurnFrame(TurnStream.ToolStart, new ToolStartEvent(
                    ev.ToolCallId ?? string.Empty,
                    ev.ToolName.Name,
                    ev.ToolArguments ?? new Dictionary<string, string?>())),

            // §5·a: `summary` (always) + the optional structured `result` card the dispatcher attached.
            AssistantEventKind.ToolResult when ev.ToolName is not null =>
                new TurnFrame(TurnStream.ToolResult, new ToolResultEvent(
                    ev.ToolCallId ?? string.Empty, ev.ToolName.Name,
                    ev.ToolSummary ?? string.Empty, ev.ToolData)),

            AssistantEventKind.Progress =>
                new TurnFrame(TurnStream.Progress, new ProgressEvent(
                    ev.ToolName?.Name ?? string.Empty,
                    ev.ProgressKey ?? string.Empty,
                    ev.ProgressLabel ?? string.Empty,
                    ev.ProgressStatus ?? "active",
                    ev.ToolCallId)),

            AssistantEventKind.Confirmation =>
                new TurnFrame(TurnStream.CommandProposed,
                    Proposed(ev.StagedConfirmation!, principal, pending, confirmationTtlSeconds, ref proposalSeq)),

            AssistantEventKind.Error =>
                new TurnFrame(TurnStream.Error,
                    new StreamErrorEvent("assistant_failed", ev.ErrorMessage ?? "The assistant failed.")),

            AssistantEventKind.Final =>
                new TurnFrame(TurnStream.Done, new DoneEvent(
                    ev.Text ?? string.Empty, DateTimeOffset.UtcNow, UsageDto.From(ev.Usage), ev.TurnId)),

            _ => null,
        };

    /// <summary>
    /// Stage the operation, bound to the verified caller, and describe it. The library only ever hands
    /// over the raw op; what the frame carries is the handle that redeems it at <c>/confirm</c> — and
    /// because that handle is scoped to the USER rather than to a session, any of that person's
    /// surfaces watching this turn can redeem it.
    /// </summary>
    private static CommandProposedEvent Proposed(
        PendingConfirmation c,
        AuthPrincipal principal,
        IPendingConfirmationStore pending,
        int confirmationTtlSeconds,
        ref int proposalSeq)
    {
        // write_file: the frame's `file` block carries the real content, so a surface can render a diff
        // before anyone approves it.
        CommandFile? file = c.Kind == ConfirmationKind.WriteFile
            && c.ConfigKey is not null && c.ConfigValue is not null
            ? new CommandFile(c.ConfigKey, c.ConfigValue)
            : null;

        return new CommandProposedEvent(
            Id: $"cmd_{proposalSeq++}",
            Verb: ApiVerb(c.Kind),
            Subject: new CommandSubject(SubjectResource(c.Kind), c.Target),
            Confirm: ComposeConfirm(c),
            Token: pending.Put(
                c, principal.UserId,
                DateTimeOffset.UtcNow.AddSeconds(Math.Max(confirmationTtlSeconds, 1))),
            Reason: null,
            // write_file's content already rides `file` above — ConfigKey/ConfigValue here stay
            // set-config's own fields, never the write payload.
            ConfigKey: c.Kind == ConfirmationKind.WriteFile ? null : c.ConfigKey,
            ConfigValue: c.Kind == ConfirmationKind.WriteFile ? null : c.ConfigValue,
            InstanceName: c.InstanceName,
            File: file);
    }

    /// <summary>The normalised §5·a API verb token for a staged kind (the SPA routes a confirm to the
    /// command path by it). Distinct from <see cref="ConfirmationKinds.Verb"/>'s human label.</summary>
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
        ConfirmationKind.OpenPorts => "open_ports",
        ConfirmationKind.WriteFile => "write_file",
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

        // write_file carries the relative path on ConfigKey and the (potentially large) new content on
        // ConfigValue — the prompt names the file, never dumps the content into the confirm text.
        if (c.Kind == ConfirmationKind.WriteFile && c.ConfigKey is not null)
            return $"Overwrite '{c.ConfigKey}' on {c.Target}?";

        // open_ports carries the port spec on ConfigValue and the optional router leg on ConfigKey
        // ("router" ⇒ also open the UPnP forward). Surface both in the prompt.
        if (c.Kind == ConfirmationKind.OpenPorts && !string.IsNullOrWhiteSpace(c.ConfigValue))
        {
            bool includeRouter = string.Equals(c.ConfigKey, "router", StringComparison.Ordinal);
            return includeRouter
                ? $"Open host-firewall + router/UPnP forward for port(s) {c.ConfigValue} on {c.Target}?"
                : $"Open host-firewall port(s) {c.ConfigValue} on {c.Target}?";
        }

        var verb = ConfirmationKinds.Verb(c.Kind); // "start", "back up", "set config on", …
        return $"{char.ToUpperInvariant(verb[0])}{verb[1..]} {c.Target}?";
    }
}
