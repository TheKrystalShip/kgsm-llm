using TheKrystalShip.Kgsm.Assistant.Audit;
using TheKrystalShip.Kgsm.Assistant.Ports;

namespace TheKrystalShip.Kgsm.Assistant.RootCause;

/// <summary>
/// The console of the run that ended at a crash — the fourth source
/// <see cref="RootCauseAggregator"/> composes, and the only one carrying what the failing process
/// itself said.
/// <para>
/// It exists because the supervisor rotates an instance's log on every fresh start: a server that
/// aborted and was restarted has its cause in the run that ended and a clean boot in the one that
/// followed. Reading "the console" gets the second. This carries the first.
/// </para>
/// </summary>
/// <param name="Crash">The crash event this console belongs to, or null when the window held none.</param>
/// <param name="Lines">
/// The crashed run's trailing output, oldest-first. Empty when there was no crash to read for, when
/// no run could be matched to it, or when the run held no output — <paramref name="State"/> tells
/// those apart from a read that failed.
/// </param>
/// <param name="State">
/// Whether the supervisor answered. <see cref="FactsState.Unavailable"/> means the console could not
/// be read at all, which is never reported as the run having printed nothing.
/// </param>
public sealed record CrashConsole(
    AuditEventRow? Crash,
    IReadOnlyList<string> Lines,
    FactsState State)
{
    /// <summary>No crash in the window, so nothing to read — distinct from a failed read.</summary>
    public static readonly CrashConsole NoCrash = new(null, [], FactsState.Available);

    /// <summary>Whether there is output from a crashed run to reason about.</summary>
    public bool HasOutput => Crash is not null && Lines.Count > 0;
}

/// <summary>
/// Picks which run of a console holds a given crash. Pure and I/O-free so the choice is testable and
/// lives beside the rules that consume it, rather than in the adapter that does the fetching.
/// </summary>
/// <remarks>
/// The match is by time, because that is the only thing the two sources share: the supervisor stamps
/// a run with when it stopped printing, and the engine stamps the crash with when the exit was
/// observed. Those are close but never equal — a process prints its last line, then the supervisor
/// notices the cgroup emptied on its next tick — so the run is matched to the crash by proximity
/// inside a bounded window rather than by equality.
/// </remarks>
public static class CrashRunSelector
{
    /// <summary>
    /// How long before the crash event a run may have stopped printing and still be that crash's
    /// run. Generous next to the supervisor's ~1s detection, because a process can go quiet a while
    /// before it actually dies; bounded, because past this the pairing stops being evidence and
    /// starts being a guess.
    /// </summary>
    public static readonly TimeSpan MaxDetectionLag = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How far AFTER the crash event a run may have last printed. A dying process cannot write once
    /// it is gone and both stamps come off the same host clock, so this tolerates ordering jitter and
    /// nothing more. Kept tight because under a crash loop the runs are seconds apart: a generous
    /// grace here reaches forward past the crash and picks the run that came AFTER it — the clean
    /// boot, which is the exact mistake this whole path exists to stop making.
    /// </summary>
    public static readonly TimeSpan ClockSkewGrace = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The index of the run that was live when <paramref name="crashAt"/> happened, or null when no
    /// run ended near enough to be it.
    /// </summary>
    /// <remarks>
    /// A run still in progress is never a candidate: it has not ended, so it cannot be the run that
    /// ended at the crash. Among the rest the latest-ending one inside the window wins — under a
    /// crash loop the runs are seconds apart, and the nearest is the one that crash belongs to.
    /// </remarks>
    public static int? Select(IReadOnlyList<ConsoleRunInfo> runs, DateTimeOffset crashAt)
    {
        ConsoleRunInfo? best = null;

        foreach (var run in runs)
        {
            if (run.EndedAt is not { } endedAt)
                continue; // in progress — not a run that ended

            if (endedAt > crashAt + ClockSkewGrace)
                continue; // ended after the crash was seen: a later run, not this one
            if (crashAt - endedAt > MaxDetectionLag)
                continue; // too long before the crash to be evidence of it

            if (best is null || endedAt > best.EndedAt)
                best = run;
        }

        return best?.Index;
    }
}
