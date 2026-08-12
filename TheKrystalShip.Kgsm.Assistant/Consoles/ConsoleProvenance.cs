using TheKrystalShip.Kgsm.Assistant.Ports;

namespace TheKrystalShip.Kgsm.Assistant.Consoles;

/// <summary>
/// The sentence that says <b>which</b> console is being read, placed above the output itself.
/// <para>
/// A console is not one continuous stream. The supervisor rotates an instance's log on every fresh
/// start, so what comes back is one run — and after a crash-restart that is the run that came
/// <em>after</em> the crash, holding a clean boot. Handed those lines with no provenance, a reader
/// concludes the server is fine and nothing went wrong, which is true of the run it was shown and
/// false of the question it was asked. The lines cannot carry that context themselves: they are the
/// bytes the game wrote, and the game does not know it was restarted.
/// </para>
/// <para>
/// Pure and I/O-free, so the wording is testable on its own and the dispatcher stays an orchestrator.
/// It states only what the run list measured — a run's ending, and how the supervisor classified it —
/// and never asserts a start time for a run in progress, which nothing measures.
/// </para>
/// </summary>
public static class ConsoleProvenance
{
    /// <summary>
    /// How recently a run must have begun for the run before it to be worth pointing at. Inside this
    /// window "read the console" is usually asked about something that has just happened, and the
    /// answer is likely to be in the run before rather than the short one on screen. Outside it, a
    /// server has been up long enough that its own output is the subject and an older run is noise.
    /// </summary>
    public static readonly TimeSpan RecentRestartWindow = TimeSpan.FromHours(2);

    /// <summary>
    /// The provenance line for the run at <paramref name="index"/>, plus — when the run in progress
    /// only just began — a sentence naming the run before it and how it ended.
    /// </summary>
    /// <param name="instance">The resolved instance name.</param>
    /// <param name="runs">The run list, newest first, as the supervisor reported it.</param>
    /// <param name="index">Which run is being read.</param>
    /// <param name="now">The current time, injected so the wording is testable.</param>
    public static string Describe(
        string instance, IReadOnlyList<ConsoleRunInfo> runs, int index, DateTimeOffset now)
    {
        // No run list to reason about (the supervisor didn't answer, or this instance keeps no runs).
        // The output is still real; it just cannot be placed, and claiming a position for it would be
        // the invention this whole type exists to prevent.
        if (index < 0 || index >= runs.Count)
            return $"Recent console output for {instance}:";

        ConsoleRunInfo run = runs[index];
        ConsoleRunInfo? previous = index + 1 < runs.Count ? runs[index + 1] : null;

        string header = run.Current
            ? CurrentRunHeader(instance, index, previous, now)
            : $"Console output for {instance} — run {index}, which {EndingClause(run)}."
              + (index == 0 ? " Nothing is running, so this is its most recent output." : "");

        string? note = RestartNote(instance, run, previous, now);
        return note is null ? header + ":" : header + "\n\n" + note + "\n";
    }

    private static string CurrentRunHeader(
        string instance, int index, ConsoleRunInfo? previous, DateTimeOffset now)
    {
        string head = $"Console output for {instance} — run {index}, the run in progress";

        // When the run began is not measured anywhere. What IS measured is when the run before it
        // stopped printing, and the next spawn follows that within about a second — so the boundary
        // is stated as the previous run's ending rather than as a start time for this one.
        if (previous?.EndedAt is { } boundary)
            return head + $". It picks up where the previous run left off at {Stamp(boundary)} "
                 + $"({Ago(now - boundary)} ago); nothing before that is in this output";

        return head + ". It is the only run on record, so this is everything the server has printed";
    }

    /// <summary>
    /// The pointer at the previous run, or null when there is nothing worth pointing at: no previous
    /// run, or a run in progress that has been up long enough to be the subject in its own right.
    /// </summary>
    private static string? RestartNote(
        string instance, ConsoleRunInfo run, ConsoleRunInfo? previous, DateTimeOffset now)
    {
        // Only the run in progress gets this. Asking for an older run by number is already asking a
        // question about a specific run, and telling that caller where the runs are is noise.
        if (!run.Current || previous?.EndedAt is not { } boundary)
            return null;

        if (now - boundary > RecentRestartWindow)
            return null;

        return $"⚠ {instance} restarted {Ago(now - boundary)} ago, so this run holds only what it has "
             + $"printed since. The run before it {EndingClause(previous)}. If you are looking into why "
             + $"{instance} went down, what it printed on the way out is in run 1 — read it by asking "
             + "for this instance's console again with run=1.";
    }

    /// <summary>
    /// How a finished run ended, in words. Built from the supervisor's own classification, so a run it
    /// never classified says exactly that rather than being described as having ended cleanly.
    /// </summary>
    private static string EndingClause(ConsoleRunInfo run)
    {
        string when = run.EndedAt is { } t ? $" at {Stamp(t)}" : "";
        string exit = run.ExitCode is { } code ? $", exit {code}" : "";

        return run.Outcome switch
        {
            ConsoleRunInfo.CrashedOutcome => $"ended in a crash{when}{exit}",
            ConsoleRunInfo.GaveUpOutcome =>
                $"ended in a crash{when}{exit}, after which the supervisor stopped retrying",
            "stopped" => $"was stopped deliberately{when}",
            "exited" => $"exited on its own{when}{exit} and was left down",
            _ => $"ended{when} — how it ended was never recorded, which is not the same as it having "
               + "ended cleanly",
        };
    }

    private static string Stamp(DateTimeOffset t) => t.UtcDateTime.ToString("u");

    /// <summary>
    /// A duration in the coarsest unit that still says something useful. Deliberately approximate:
    /// the reader needs to know whether a restart was minutes or days ago, and a precise figure here
    /// would invite it to be quoted as one.
    /// </summary>
    private static string Ago(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;

        return span.TotalMinutes < 1 ? "less than a minute"
            : span.TotalHours < 1 ? Plural((int)span.TotalMinutes, "minute")
            : span.TotalDays < 1 ? Plural((int)span.TotalHours, "hour")
            : Plural((int)span.TotalDays, "day");
    }

    private static string Plural(int n, string unit) => n == 1 ? $"1 {unit}" : $"{n} {unit}s";
}
