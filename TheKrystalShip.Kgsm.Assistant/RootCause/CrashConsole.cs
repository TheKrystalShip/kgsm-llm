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
/// <param name="ExitCode">
/// The code the crashed run's leader exited with, as the supervisor read it, or null where it could
/// not be read. It is evidence and never a verdict on its own — game servers routinely exit 0 on a
/// fatal error — but a signal code says something the console cannot: that the process was killed
/// from outside rather than failing on its own terms.
/// </param>
public sealed record CrashConsole(
    AuditEventRow? Crash,
    IReadOnlyList<string> Lines,
    FactsState State,
    int? ExitCode = null)
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
/// <para>
/// <b>A run the supervisor marked crashed is preferred over one that merely ended nearby.</b> The
/// supervisor is the only thing that watched the process exit and can tell that exit from a
/// deliberate stop, and it records that verdict against the run. Time is still what pairs a marked
/// run with a particular crash — two crashes in a loop are both marked — but it no longer has to
/// carry the question of whether a crash happened at all.
/// </para>
/// <para>
/// <b>Time-only matching remains, for every run the supervisor never classified.</b> A run that
/// predates the ledger, or one that ended while the daemon was down, reports its outcome as unknown
/// — and unknown is not "did not crash". Falling back to proximity keeps those readable instead of
/// making an absent record look like an absent crash.
/// </para>
/// <para>
/// The timestamps are close but never equal — a process prints its last line, then the supervisor
/// notices the cgroup emptied on its next tick — so a run is paired with a crash by proximity inside
/// a bounded window, never by equality.
/// </para>
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
    /// ended at the crash. Among the rest, a run the supervisor marked as crashed wins over one it
    /// did not, and within either group the latest-ending one inside the window wins — under a crash
    /// loop the runs are seconds apart, and the nearest is the one that crash belongs to.
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

            if (best is null || Beats(run, best))
                best = run;
        }

        return best?.Index;
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is the better answer than <paramref name="incumbent"/>.
    /// <para>
    /// A run the supervisor watched fail outranks one that only happens to have ended nearby, whatever
    /// their timestamps say — that is a measured verdict against a coincidence. This is what stops a
    /// server stopped and restarted moments before an unrelated crash from donating its console to it.
    /// Between two runs of equal standing, the later one wins: it is the one nearer the crash.
    /// </para>
    /// </summary>
    private static bool Beats(ConsoleRunInfo candidate, ConsoleRunInfo incumbent) =>
        candidate.Crashed != incumbent.Crashed
            ? candidate.Crashed
            : candidate.EndedAt > incumbent.EndedAt;
}
