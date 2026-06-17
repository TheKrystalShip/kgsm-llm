namespace TheKrystalShip.Kgsm.Assistant.Cli;

/// <summary>
/// A transient one-line "working" indicator animated on a background task and written to
/// <b>stderr</b>. Interactive only — the host constructs it solely when stderr is a TTY, so it never
/// animates carriage-return frames into a redirected stream.
///
/// The sole correctness rule is that no frame may be written after the line is erased. So every
/// frame write <i>and</i> the erase happen under one lock, and the frame loop re-checks
/// <see cref="_running"/> while holding it — which is enough on its own; no join with the loop task
/// is needed.
/// </summary>
internal sealed class Spinner : IDisposable
{
    private static readonly string[] Frames =
        { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(90);

    private readonly TextWriter _err;
    private readonly bool _color;
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private bool _running;
    private int _frame;

    public Spinner(TextWriter err, bool color)
    {
        _err = err;
        _color = color;
    }

    /// <summary>Starts the animation with the given label (e.g. "thinking"); a no-op if already running.</summary>
    public void Start(string label)
    {
        lock (_gate)
        {
            if (_running)
                return;
            _running = true;
            _frame = 0;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _ = Task.Run(() => LoopAsync(label, token));
        }
    }

    /// <summary>Stops the animation and erases the line. Idempotent; safe to call when not running.</summary>
    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (!_running)
                return;
            _running = false;
            cts = _cts;
            _cts = null;
            _err.Write("\r\x1b[K");   // CR + clear-to-EOL, under the lock so no frame can race it
            _err.Flush();
        }
        cts?.Cancel();
        cts?.Dispose();
    }

    public void Dispose() => Stop();

    private async Task LoopAsync(string label, CancellationToken token)
    {
        try
        {
            while (true)
            {
                lock (_gate)
                {
                    if (!_running)
                        return;
                    var glyph = Frames[_frame++ % Frames.Length];
                    var line = $"\r{glyph} {label}…";
                    _err.Write(_color ? $"\x1b[2m{line}\x1b[0m" : line);
                    _err.Flush();
                }
                await Task.Delay(Interval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // expected on Stop()
        }
        catch
        {
            // cosmetic: a spinner hiccup must never take down the turn
        }
    }
}
