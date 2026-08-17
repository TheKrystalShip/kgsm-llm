using System.Globalization;

namespace TheKrystalShip.Kgsm.Assistant;

/// <summary>
/// How long ago something happened, and how a moment is written down.
/// <para>
/// A tool result is the only thing the model reads, and it holds no clock. Handed
/// <c>2026-08-17 02:32</c> it cannot tell whether that is last night or last year, so it either
/// declines to say or supplies a "now" from its own training — which is the fabrication this whole
/// surface exists to avoid. Every timestamp a tool reports therefore carries its distance from now
/// alongside the moment itself: the distance answers "is this recent", the moment survives being
/// quoted.
/// </para>
/// </summary>
public static class Elapsed
{
    /// <summary>
    /// A duration in the coarsest unit that still says something useful. Deliberately approximate:
    /// a reader needs to know whether something was minutes or days ago, and a precise figure here
    /// would invite it to be quoted as one.
    /// </summary>
    public static string Ago(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;

        return span.TotalMinutes < 1 ? "less than a minute"
            : span.TotalHours < 1 ? Plural((int)span.TotalMinutes, "minute")
            : span.TotalDays < 1 ? Plural((int)span.TotalHours, "hour")
            : Plural((int)span.TotalDays, "day");
    }

    /// <summary>
    /// A moment written as host-local time with its UTC offset, plus how long ago it was — e.g.
    /// <c>9 hours ago (2026-08-17 04:32 +02:00)</c>.
    /// <para>
    /// The offset is spelled out per timestamp rather than named once, so a reading spanning a DST
    /// boundary stays true and an operator comparing it against their own wall clock never has to
    /// guess which frame it is in.
    /// </para>
    /// </summary>
    public static string Moment(DateTimeOffset when, DateTimeOffset now) =>
        $"{Ago(now - when)} ago ({Stamp(when)})";

    /// <summary>The moment alone, host-local with its offset. For a reading that is not in the past.</summary>
    public static string Stamp(DateTimeOffset when) =>
        when.ToLocalTime().ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture);

    private static string Plural(int n, string unit) => n == 1 ? $"1 {unit}" : $"{n} {unit}s";
}
