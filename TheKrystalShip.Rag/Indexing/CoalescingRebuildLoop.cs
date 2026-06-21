using System.Threading.Channels;

using Microsoft.Extensions.Logging;

namespace TheKrystalShip.Rag.Indexing;

/// <summary>
/// Serializes filesystem-change notifications into at-most-one running re-index, coalescing bursts.
/// A bounded(1)/drop-write channel is the whole trick: many <see cref="Signal"/>s collapse to a
/// single pending token, so an editor that fires five events for one save produces one rebuild.
/// <para>
/// The loop is: block for a signal → wait out a <em>settle</em> window (debounce the burst) → drain
/// every signal that arrived during the window into this run → rebuild. A signal that lands
/// <em>after</em> the drain (e.g. while the rebuild is mid-flight) survives in the channel and
/// re-triggers on the next iteration — so changes are never lost, at the cost of at most one extra
/// (idempotent) rebuild. The settle delegate is injected so this is unit-testable without sleeps:
/// the daemon passes <c>ct =&gt; Task.Delay(window, ct)</c>; a test passes a gate it controls.
/// </para>
/// <para>A rebuild that throws is logged and the loop continues — a transient embedder outage must
/// not kill the daemon; the next change retries. Cancellation ends the loop cleanly.</para>
/// </summary>
internal sealed class CoalescingRebuildLoop
{
    // Capacity 1 + DropWrite: the queue is a single "dirty" bit. Writes past the one slot are
    // dropped (the work is already pending), which is exactly the coalescing we want.
    private readonly Channel<byte> _signals = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private readonly Func<CancellationToken, Task> _rebuild;
    private readonly Func<CancellationToken, Task> _settle;
    private readonly ILogger _logger;

    public CoalescingRebuildLoop(
        Func<CancellationToken, Task> rebuild,
        Func<CancellationToken, Task> settle,
        ILogger logger)
    {
        _rebuild = rebuild ?? throw new ArgumentNullException(nameof(rebuild));
        _settle = settle ?? throw new ArgumentNullException(nameof(settle));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Requests a rebuild. Non-blocking; coalesces with any already-pending request.</summary>
    public void Signal() => _signals.Writer.TryWrite(0);

    /// <summary>Runs until <paramref name="cancellationToken"/> fires. One rebuild at a time.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Wait for at least one change.
                await _signals.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

                // Let the burst settle, then fold every event seen so far into this single run.
                await _settle(cancellationToken).ConfigureAwait(false);
                while (_signals.Reader.TryRead(out _)) { }

                await _rebuild(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never-die: a rebuild failure (embedder down, write error) is logged and the loop
                // keeps watching. The rebuild delegate already logs its own structured Result error;
                // this backstops anything that escapes as an exception.
                _logger.LogError(ex, "Re-index attempt failed; the watcher continues and will retry on the next change.");
            }
        }
    }
}
