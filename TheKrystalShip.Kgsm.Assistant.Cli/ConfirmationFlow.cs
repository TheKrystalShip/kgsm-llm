using TheKrystalShip.Kgsm.Assistant;
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
        _ => $"{ConfirmationKinds.Verb(c.Kind)} '{c.Target}'",
    };

    private static bool IsYes(string? answer)
    {
        var a = answer?.Trim();
        return string.Equals(a, "y", StringComparison.OrdinalIgnoreCase)
            || string.Equals(a, "yes", StringComparison.OrdinalIgnoreCase);
    }
}
