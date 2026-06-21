using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using TheKrystalShip.Rag.Indexing;

namespace TheKrystalShip.Rag.Tests;

/// <summary>
/// The coalescing/serialization guarantees of the watch loop, tested deterministically (no sleeps):
/// the <em>settle</em> step and the rebuild are injected as handshakes the test drives, so each
/// property — collapse a burst, never lose a change that lands mid-rebuild, survive a throwing
/// rebuild, exit cleanly on cancel — is asserted without racing the wall clock.
/// </summary>
public sealed class CoalescingRebuildLoopTests
{
    [Fact]
    public async Task A_burst_of_signals_during_the_settle_window_collapses_to_one_rebuild()
    {
        var settleEntered = new SemaphoreSlim(0);
        var releaseSettle = new SemaphoreSlim(0);
        var rebuildEntered = new SemaphoreSlim(0);
        var releaseRebuild = new SemaphoreSlim(0);
        var rebuilds = 0;

        Func<CancellationToken, Task> settle = async ct => { settleEntered.Release(); await releaseSettle.WaitAsync(ct); };
        Func<CancellationToken, Task> rebuild = async ct =>
        {
            Interlocked.Increment(ref rebuilds);
            rebuildEntered.Release();
            await releaseRebuild.WaitAsync(ct);
        };

        using var cts = new CancellationTokenSource();
        var loop = new CoalescingRebuildLoop(rebuild, settle, NullLogger.Instance);
        var run = loop.RunAsync(cts.Token);

        loop.Signal();                                  // first change
        await settleEntered.WaitAsync();                // consumed → now settling
        loop.Signal(); loop.Signal(); loop.Signal();    // burst while settling → coalesced into this run
        releaseSettle.Release();                         // settle finishes; loop drains the burst, then rebuilds
        await rebuildEntered.WaitAsync();               // exactly one rebuild started

        cts.Cancel();                                   // stop after this rebuild — nothing is queued behind it
        releaseRebuild.Release();
        await run;

        rebuilds.Should().Be(1, "the burst was drained into the single rebuild");
    }

    [Fact]
    public async Task A_signal_that_lands_during_a_rebuild_triggers_exactly_one_more()
    {
        var rebuildEntered = new SemaphoreSlim(0);
        var releaseRebuild = new SemaphoreSlim(0);
        var rebuilds = 0;

        Func<CancellationToken, Task> settle = _ => Task.CompletedTask;
        Func<CancellationToken, Task> rebuild = async ct =>
        {
            Interlocked.Increment(ref rebuilds);
            rebuildEntered.Release();
            await releaseRebuild.WaitAsync(ct);
        };

        using var cts = new CancellationTokenSource();
        var loop = new CoalescingRebuildLoop(rebuild, settle, NullLogger.Instance);
        var run = loop.RunAsync(cts.Token);

        loop.Signal();
        await rebuildEntered.WaitAsync();   // rebuild #1 running
        loop.Signal();                      // a change arrives mid-rebuild → must not be lost
        releaseRebuild.Release();           // #1 finishes; loop picks up the queued change
        await rebuildEntered.WaitAsync();   // rebuild #2 started

        rebuilds.Should().Be(2);

        cts.Cancel();
        releaseRebuild.Release();
        await run;
    }

    [Fact]
    public async Task A_throwing_rebuild_does_not_kill_the_loop()
    {
        var firstEntered = new SemaphoreSlim(0);
        var secondEntered = new SemaphoreSlim(0);
        var attempts = 0;

        Func<CancellationToken, Task> settle = _ => Task.CompletedTask;
        Func<CancellationToken, Task> rebuild = _ =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                firstEntered.Release();
                throw new InvalidOperationException("boom");
            }
            secondEntered.Release();
            return Task.CompletedTask;
        };

        using var cts = new CancellationTokenSource();
        var loop = new CoalescingRebuildLoop(rebuild, settle, NullLogger.Instance);
        var run = loop.RunAsync(cts.Token);

        loop.Signal();
        await firstEntered.WaitAsync();     // #1 consumed (channel slot freed) and about to throw
        loop.Signal();                      // safe to enqueue now; the loop will read it after recovering
        await secondEntered.WaitAsync();    // #2 ran → the loop survived the throw

        attempts.Should().Be(2);

        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task Cancellation_with_no_pending_work_exits_cleanly()
    {
        var rebuilds = 0;
        Func<CancellationToken, Task> settle = _ => Task.CompletedTask;
        Func<CancellationToken, Task> rebuild = _ => { Interlocked.Increment(ref rebuilds); return Task.CompletedTask; };

        using var cts = new CancellationTokenSource();
        var loop = new CoalescingRebuildLoop(rebuild, settle, NullLogger.Instance);
        var run = loop.RunAsync(cts.Token);

        cts.Cancel();
        await run;   // completes (not faulted) — the OCE is swallowed by the loop

        rebuilds.Should().Be(0);
    }
}
