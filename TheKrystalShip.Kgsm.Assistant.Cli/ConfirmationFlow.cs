using System.Diagnostics;

using TheKrystalShip.Kgsm.Assistant;
using TheKrystalShip.Kgsm.Assistant.Blueprints;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Kgsm;

namespace TheKrystalShip.Kgsm.Assistant.Cli;

/// <summary>
/// Drains the destructive operations the assistant staged this turn (§5). Each is rendered
/// human-readably on <b>stderr</b> (stdout stays the model's reply) and gated:
/// <list type="bullet">
///   <item><b>Interactive stdin</b> (a TTY): prompt <c>[y/N]</c>; on yes call
///   <see cref="IServerAssistant.ConfirmAsync"/> (which re-checks authority + re-validates the
///   target against live inventory), print the outcome, then force a cache refresh via
///   <see cref="IInventoryInvalidation.Invalidate"/> so the next read is fresh (L6).</item>
///   <item><b>Non-interactive stdin</b> (piped/scripted): print the proposal and DO NOT run it
///   (L8). A scripting <c>--yes</c> escape hatch is deferred to V2.</item>
/// </list>
/// Authority is still passed in; a <c>--read-only</c> session never stages actions in the first
/// place, so this only runs for authorized interactive sessions — but ConfirmAsync re-checks anyway.
/// </summary>
internal static class ConfirmationFlow
{
    /// <summary>
    /// Processes every staged confirmation. Returns false if an executed action failed (so the
    /// caller can set a non-zero exit); a proposal that was only printed (non-interactive) or
    /// declined is not a failure.
    /// </summary>
    public static async Task<bool> DrainAsync(
        IReadOnlyList<PendingConfirmation> confirmations,
        IServerAssistant assistant,
        IInventoryInvalidation inventory,
        bool canPerformActions,
        bool interactiveStdin,
        bool color,
        TextReader input,
        TextWriter err,
        CancellationToken cancellationToken)
    {
        var ok = true;

        foreach (var confirmation in confirmations)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            var description = Describe(confirmation);
            var destructive = ConfirmationKinds.IsDestructive(confirmation.Kind);
            var marker = Ansi.Paint("⚠ Proposed action:", destructive ? Ansi.Red : Ansi.Yellow, color);

            // A drafted blueprint isn't a one-line y/N — it opens the draft in $EDITOR for the mandatory
            // review, then test-installs the saved YAML (the CLI parity of the in-chat Monaco card, incl.
            // the repair-exhaustion re-edit loop). Needs an interactive terminal.
            if (confirmation.Kind == ConfirmationKind.Blueprint)
            {
                if (!interactiveStdin)
                {
                    err.WriteLine($"{marker} {description}");
                    err.WriteLine(Ansi.Paint(
                        "  not run: reviewing a drafted blueprint needs an interactive terminal.", Ansi.Dim, color));
                    continue;
                }
                if (!await DrainBlueprintAsync(confirmation, assistant, inventory, canPerformActions, color, input, err, cancellationToken))
                    ok = false;
                continue;
            }

            if (!interactiveStdin)
            {
                // L8: scripted/piped stdin never auto-confirms. Show the proposal; do not execute.
                err.WriteLine($"{marker} {description}");
                err.WriteLine(Ansi.Paint(
                    "  not run: stdin is not interactive (run in a terminal to confirm).", Ansi.Dim, color));
                continue;
            }

            err.WriteLine($"{marker} {description}");
            err.Write("  Proceed? [y/N] ");
            err.Flush();

            var answer = input.ReadLine();
            if (cancellationToken.IsCancellationRequested)
                break;

            if (!IsYes(answer))
            {
                err.WriteLine(Ansi.Paint("  skipped.", Ansi.Dim, color));
                continue;
            }

            var result = await assistant.ConfirmAsync(confirmation, canPerformActions, cancellationToken);

            // A confirmed action may have changed inventory whether it reported success or not;
            // invalidate so the next inventory read re-fetches from kgsm (L6).
            inventory.Invalidate();

            if (result.IsSuccess)
            {
                err.WriteLine(Ansi.Paint($"  ✓ {result.Value}", Ansi.Dim, color));
            }
            else
            {
                err.WriteLine(Ansi.Paint($"  ✗ {result.Error}", Ansi.Red, color));
                ok = false;
            }
        }

        return ok;
    }

    /// <summary>Human-readable one-liner for a staged op, e.g. <c>uninstall 'terraria'</c>.</summary>
    private static string Describe(PendingConfirmation c) => c.Kind switch
    {
        ConfirmationKind.Install => string.IsNullOrWhiteSpace(c.InstanceName)
            ? $"install '{c.Target}'"
            : $"install '{c.Target}' as '{c.InstanceName}'",
        ConfirmationKind.SetConfig =>
            $"set config on '{c.Target}' ({c.ConfigKey}={c.ConfigValue})",
        // The content itself isn't printed (it can be sizeable) — just what's being replaced and
        // how much; the CLI's y/N is the confirmation, not a diff viewer (owed to a later surface).
        ConfirmationKind.WriteFile =>
            $"overwrite '{c.ConfigKey}' on '{c.Target}' ({(c.ConfigValue?.Length ?? 0):N0} chars)",
        ConfirmationKind.Blueprint =>
            $"review + test-install the drafted blueprint for '{(string.IsNullOrWhiteSpace(c.InstanceName) ? c.Target : c.InstanceName)}'",
        _ => $"{ConfirmationKinds.Verb(c.Kind)} '{c.Target}'",
    };

    /// <summary>
    /// The blueprint-review checkpoint on the CLI (assistant-blueprint-review-plan.md P4): open the
    /// drafted YAML in <c>$EDITOR</c> for the mandatory human review, then finalize the saved text —
    /// re-validate → test-install → boot + verify → keep. The surface-agnostic parity of the in-chat
    /// Monaco card, including the recovery loop: when the autonomous repair exhausts, the assistant
    /// returns a fresh draft plus the boot log, and this re-opens the editor for another pass. Saving an
    /// empty file (or declining the test-install) abandons — never a failure. Returns false only on a real
    /// finalize failure or an editor error.
    /// </summary>
    private static async Task<bool> DrainBlueprintAsync(
        PendingConfirmation confirmation,
        IServerAssistant assistant,
        IInventoryInvalidation inventory,
        bool canPerformActions,
        bool color,
        TextReader input,
        TextWriter err,
        CancellationToken ct)
    {
        var game = string.IsNullOrWhiteSpace(confirmation.InstanceName) ? confirmation.Target : confirmation.InstanceName;
        var draft = confirmation.ConfigValue ?? string.Empty;
        string? evidence = null;

        // A runaway backstop only — the user drives the loop and abandons by emptying the file; finalize is
        // minutes per round, so this bound is never reached by an intentional edit session.
        const int maxRounds = 8;
        for (var round = 0; round < maxRounds; round++)
        {
            if (ct.IsCancellationRequested)
                return true;

            err.WriteLine(Ansi.Paint($"⚠ Review the drafted blueprint for '{game}'.", Ansi.Yellow, color));
            if (evidence is not null)
            {
                err.WriteLine(Ansi.Paint("  The last attempt didn’t boot — its log:", Ansi.Red, color));
                foreach (var line in evidence.Replace("\r\n", "\n").Split('\n'))
                    err.WriteLine(Ansi.Paint("    " + line.TrimEnd(), Ansi.Dim, color));
            }
            err.WriteLine(Ansi.Paint(
                "  Opening it in your editor — save your edits to test-install, or empty the file to abandon.", Ansi.Dim, color));
            err.Flush();

            string edited;
            try
            {
                edited = await EditInEditorAsync(draft, ct);
            }
            catch (Exception ex)
            {
                err.WriteLine(Ansi.Paint($"  ✗ couldn’t open an editor ({ex.Message}). Set $EDITOR and try again.", Ansi.Red, color));
                return false;
            }

            if (string.IsNullOrWhiteSpace(edited))
            {
                err.WriteLine(Ansi.Paint("  abandoned (empty draft) — nothing was added.", Ansi.Dim, color));
                return true;   // abandon is not a failure
            }

            err.Write("  Test-install and verify this? [y/N] ");
            err.Flush();
            if (!IsYes(input.ReadLine()) || ct.IsCancellationRequested)
            {
                err.WriteLine(Ansi.Paint("  skipped — nothing was added.", Ansi.Dim, color));
                return true;
            }

            err.WriteLine(Ansi.Paint("  test-installing and verifying… this can take a few minutes.", Ansi.Dim, color));
            err.Flush();

            var result = await assistant.FinalizeBlueprintAsync(game, edited, canPerformActions, ct);
            // Finalize test-installs (and tears down) — inventory may have moved whatever the outcome.
            inventory.Invalidate();
            var data = result.Data;

            if (data?.Outcome == BlueprintAuthoringOutcome.Verified)
            {
                err.WriteLine(Ansi.Paint($"  ✓ {result.Summary}", Ansi.Dim, color));
                return true;
            }
            if (data?.Outcome == BlueprintAuthoringOutcome.DraftReady && data.DraftYaml is not null)
            {
                // Repair exhausted (or the edit didn’t validate) — loop back into the editor with the
                // returned draft and, when present, the boot log that explains why it didn’t come up.
                draft = data.DraftYaml;
                evidence = data.Evidence;
                err.WriteLine(Ansi.Paint($"  … {result.Summary}", Ansi.Dim, color));
                continue;
            }

            err.WriteLine(Ansi.Paint($"  ✗ {result.Summary}", Ansi.Red, color));
            return false;
        }

        err.WriteLine(Ansi.Paint("  stopped after several attempts — nothing was added.", Ansi.Dim, color));
        return false;
    }

    /// <summary>
    /// Opens <paramref name="seed"/> in the user's editor (<c>$VISUAL</c> → <c>$EDITOR</c> → <c>nano</c>)
    /// on a temp <c>.bp.yaml</c> file and returns the saved contents. The editor string is split on
    /// whitespace so a command with flags (e.g. <c>code -w</c>) works — the file path is appended last.
    /// The temp file is always cleaned up.
    /// </summary>
    private static async Task<string> EditInEditorAsync(string seed, CancellationToken ct)
    {
        var editor = Environment.GetEnvironmentVariable("VISUAL");
        if (string.IsNullOrWhiteSpace(editor))
            editor = Environment.GetEnvironmentVariable("EDITOR");
        if (string.IsNullOrWhiteSpace(editor))
            editor = "nano";

        var path = Path.Combine(Path.GetTempPath(), $"kgsm-draft-{Guid.NewGuid():N}.bp.yaml");
        await File.WriteAllTextAsync(path, seed, ct);
        try
        {
            var parts = editor.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var psi = new ProcessStartInfo { FileName = parts[0], UseShellExecute = false };
            for (var i = 1; i < parts.Length; i++)
                psi.ArgumentList.Add(parts[i]);
            psi.ArgumentList.Add(path);

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException($"could not start editor '{editor}'");
            await proc.WaitForExitAsync(ct);
            return await File.ReadAllTextAsync(path, ct);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    private static bool IsYes(string? answer)
    {
        var a = answer?.Trim();
        return string.Equals(a, "y", StringComparison.OrdinalIgnoreCase)
            || string.Equals(a, "yes", StringComparison.OrdinalIgnoreCase);
    }
}
