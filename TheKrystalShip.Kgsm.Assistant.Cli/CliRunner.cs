using TheKrystalShip.Kgsm.Assistant;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Kgsm;

namespace TheKrystalShip.Kgsm.Assistant.Cli;

/// <summary>
/// Runs a single assistant turn end to end: stream it through the <see cref="TerminalRenderer"/>,
/// then drain any staged confirmations (<see cref="ConfirmationFlow"/>). Shared by all three entry
/// forms (one-shot arg, one-shot stdin, REPL) so they render and confirm identically. Carries the
/// per-session policy + presentation flags; a fresh renderer is created per turn.
/// </summary>
internal sealed class CliRunner
{
    private readonly IServerAssistant _assistant;
    private readonly IInventoryInvalidation _inventory;
    private readonly bool _canPerformActions;
    private readonly bool _interactiveStdin;
    private readonly bool _showStatus;
    private readonly bool _color;

    public CliRunner(
        IServerAssistant assistant,
        IInventoryInvalidation inventory,
        bool canPerformActions,
        bool interactiveStdin,
        bool showStatus,
        bool color)
    {
        _assistant = assistant;
        _inventory = inventory;
        _canPerformActions = canPerformActions;
        _interactiveStdin = interactiveStdin;
        _showStatus = showStatus;
        _color = color;
    }

    /// <summary>
    /// Streams + renders one turn, then drains its confirmations. Returns false if the turn ended
    /// in an Error event or an executed action failed (the caller maps that to a non-zero exit).
    /// Propagates <see cref="OperationCanceledException"/> on Ctrl-C so the caller decides whether
    /// to abort (one-shot) or continue the loop (REPL).
    /// </summary>
    public async Task<bool> RunTurnAsync(string conversationId, string prompt, CancellationToken cancellationToken)
    {
        var renderer = new TerminalRenderer(Console.Out, Console.Error, _showStatus, _color);

        await foreach (var ev in _assistant
                           .RunStreamAsync(conversationId, prompt, _canPerformActions, cancellationToken))
            renderer.Handle(ev);

        if (renderer.HadError)
            return false;

        if (renderer.Confirmations.Count > 0)
            return await ConfirmationFlow.DrainAsync(
                renderer.Confirmations, _assistant, _inventory, _canPerformActions,
                _interactiveStdin, _color, Console.In, Console.Error, cancellationToken);

        return true;
    }
}
