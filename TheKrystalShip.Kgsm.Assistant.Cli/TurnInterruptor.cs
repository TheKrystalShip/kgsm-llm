namespace TheKrystalShip.Kgsm.Assistant.Cli;

/// <summary>
/// Owns the single process-wide Ctrl-C (SIGINT) handler and turns it into per-turn cancellation:
/// while a turn runs, Ctrl-C cancels just that turn (aborting Ollama generation, L3) instead of
/// killing the process; with no turn running (e.g. at the REPL prompt) Ctrl-C is left to its
/// default — the process exits. Both the one-shot and REPL paths run their turns through here.
/// </summary>
internal sealed class TurnInterruptor : IDisposable
{
    private volatile CancellationTokenSource? _active;
    private readonly ConsoleCancelEventHandler _handler;

    public TurnInterruptor()
    {
        _handler = (_, e) =>
        {
            var active = _active;
            if (active is not null && !active.IsCancellationRequested)
            {
                e.Cancel = true;   // a turn is running: cancel it, don't terminate the process
                active.Cancel();
            }
            // else: no active turn → leave e.Cancel false so SIGINT exits (e.g. waiting at the prompt)
        };
        Console.CancelKeyPress += _handler;
    }

    /// <summary>
    /// Runs <paramref name="body"/> under a fresh cancellation token that Ctrl-C cancels.
    /// Returns <c>Completed = false</c> if it was cancelled, otherwise the body's result. The body
    /// is expected to honor the token and surface cancellation as <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<(bool Completed, T Result)> RunAsync<T>(Func<CancellationToken, Task<T>> body)
    {
        using var cts = new CancellationTokenSource();
        _active = cts;
        try
        {
            var result = await body(cts.Token);
            return (true, result);
        }
        catch (OperationCanceledException)
        {
            return (false, default!);
        }
        finally
        {
            _active = null;
        }
    }

    public void Dispose() => Console.CancelKeyPress -= _handler;
}
