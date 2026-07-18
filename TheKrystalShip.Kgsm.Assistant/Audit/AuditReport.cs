using TheKrystalShip.Kgsm.Assistant.Envelope;
using TheKrystalShip.Kgsm.Assistant.Ports;

namespace TheKrystalShip.Kgsm.Assistant.Audit;

/// <summary>
/// Resolves the model-supplied <c>window</c>/<c>range</c> vocabulary (<c>1h</c>/<c>24h</c>/<c>7d</c>/
/// <c>30d</c> — the same set <c>get_performance</c> uses) into a <c>since</c> unix-ms bound for the
/// monitor's <c>GET /events</c>. An unrecognized or omitted value is not an error — it honestly
/// substitutes the caller's default so a confused/older model still gets a sensible read, and the
/// normalized label (not the raw request) is what the summary and card report.
/// </summary>
public static class AuditWindow
{
    public const string DefaultAuditWindow = "24h";
    public const string DefaultChangeRange = "7d";

    private static readonly IReadOnlyDictionary<string, TimeSpan> Spans =
        new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase)
        {
            ["1h"] = TimeSpan.FromHours(1),
            ["24h"] = TimeSpan.FromHours(24),
            ["7d"] = TimeSpan.FromDays(7),
            ["30d"] = TimeSpan.FromDays(30),
        };

    /// <summary>The recognized window tokens, for the tool's schema <c>enum</c> constraint.</summary>
    public static IReadOnlyList<string> AllowedValues { get; } = new[] { "1h", "24h", "7d", "30d" };

    /// <summary>Resolves <paramref name="requested"/> against <paramref name="fallback"/> and
    /// <paramref name="now"/>, returning the normalized label and the <c>since</c> bound to query.</summary>
    public static (string Label, long SinceMs) Resolve(string? requested, string fallback, DateTimeOffset now)
    {
        var key = string.IsNullOrWhiteSpace(requested) ? fallback : requested.Trim();
        if (!Spans.TryGetValue(key, out var span))
        {
            key = fallback;
            span = Spans[fallback];
        }

        return (key, now.Subtract(span).ToUnixTimeMilliseconds());
    }
}

/// <summary>
/// The deterministic <c>get_audit_log</c> / <c>get_change_timeline</c> composer (mirrors
/// <see cref="Metrics.PerformanceReport"/>): turns a neutral <see cref="Ports.EventHistoryReading"/>
/// into an <see cref="AuditData"/> card plus a model-grounding <see cref="ToolResult{TData}.Summary"/>.
/// Pure and I/O-free (the socket fetch happens in the port impl), so it's unit-testable without mocks
/// and is the single home for how an event window is worded.
/// <para>
/// Honesty rules baked in: a <see cref="AuditReadState.MonitorUnavailable"/> read is an honest
/// "couldn't read" — explicitly NOT a claim that nothing happened; an
/// <see cref="AuditReadState.Available"/> read with zero rows is a real, measured "no events
/// recorded"; and an event with no <see cref="AuditEventRow.Actor"/> is reported as unknown, never
/// defaulted to a placeholder like "system" (the never-fabricate rule).
/// </para>
/// </summary>
public static class AuditReport
{
    /// <summary>
    /// The state-changing subset <c>get_change_timeline</c> filters to — durable changes to a
    /// server's existence, version, or exposed configuration. Deliberately EXCLUDES:
    /// <c>instance_started</c>/<c>instance_stopped</c> (routine run-state flips, not a change to the
    /// server itself — <c>get_audit_log</c>/<c>get_status</c> are the right tools for those),
    /// <c>instance_crashed</c> (a fault/operational event for root-cause tracing, not a deliberate
    /// change), and <c>instance_player_joined</c>/<c>instance_player_left</c> (player activity, not
    /// server state). An event type this host has never seen (a future kgsm event) is honestly left
    /// OUT of the timeline rather than guessed at — it still shows up in <c>get_audit_log</c>.
    /// </summary>
    public static readonly IReadOnlySet<string> ChangeEventTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "instance_installed",
        "instance_uninstalled",
        "instance_updated",
        "instance_version_updated",
        "instance_backup_created",
        "instance_ports_opened",
        "instance_ports_closed",
    };

    /// <summary>Friendly (singular, plural) labels for the raw kgsm event-type vocabulary the
    /// deterministic summary counts by type. An event type not in this map (a future/unknown kgsm
    /// event) falls back to its raw type string for BOTH forms — honest, never a guessed grammar.</summary>
    private static readonly IReadOnlyDictionary<string, (string One, string Many)> TypeLabels =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["instance_started"] = ("start", "starts"),
            ["instance_stopped"] = ("stop", "stops"),
            ["instance_crashed"] = ("crash", "crashes"),
            ["instance_updated"] = ("update", "updates"),
            ["instance_installed"] = ("install", "installs"),
            ["instance_uninstalled"] = ("uninstall", "uninstalls"),
            ["instance_version_updated"] = ("version update", "version updates"),
            ["instance_backup_created"] = ("backup", "backups"),
            ["instance_ports_opened"] = ("port-open event", "port-open events"),
            ["instance_ports_closed"] = ("port-close event", "port-close events"),
            ["instance_player_joined"] = ("player join", "player joins"),
            ["instance_player_left"] = ("player leave", "player leaves"),
        };

    /// <summary>
    /// Builds the <c>get_audit_log</c> result: every event in the window, unfiltered by type.
    /// </summary>
    public static ToolResult<AuditData> Build(EventHistoryReading reading, string? instance, string window)
    {
        var subject = instance ?? "primary";
        var data = new AuditData(instance, window, reading.State, reading.Events);

        var (confidence, summary) = reading.State switch
        {
            AuditReadState.Available => (Confidence.Confirmed, BuildSummary(instance, window, reading.Events,
                emptyWording: $"No events recorded for {(instance ?? "any server")} in the last {window}.")),
            _ => (Confidence.Possible,
                $"Event history for {(instance ?? "this host")} is unavailable right now — the metrics " +
                "monitor isn't reachable. That isn't a sign nothing happened; the events just couldn't be read."),
        };

        return new ToolResult<AuditData>(
            Tool: LlmTools.GetAuditLog,
            Confidence: confidence,
            Subject: new ResultRef(ResourceKind.Audit, subject),
            Summary: summary,
            Data: data);
    }

    /// <summary>
    /// Builds the <c>get_change_timeline</c> result: the same source, filtered to
    /// <see cref="ChangeEventTypes"/> and framed as "what changed" rather than "what happened".
    /// </summary>
    public static ToolResult<AuditData> BuildChangeTimeline(EventHistoryReading reading, string? instance, string range)
    {
        var subject = instance ?? "primary";
        var changes = reading.State == AuditReadState.Available
            ? reading.Events.Where(e => ChangeEventTypes.Contains(e.Type)).ToArray()
            : Array.Empty<AuditEventRow>();
        var data = new AuditData(instance, range, reading.State, changes);

        var (confidence, summary) = reading.State switch
        {
            AuditReadState.Available => (Confidence.Confirmed, BuildSummary(instance, range, changes,
                emptyWording: $"No changes recorded for {(instance ?? "any server")} in the last {range} " +
                "(installs, uninstalls, updates, version changes, backups, and port changes count as " +
                "changes; routine starts/stops and player activity don't).",
                changeFraming: true)),
            _ => (Confidence.Possible,
                $"The change timeline for {(instance ?? "this host")} is unavailable right now — the metrics " +
                "monitor isn't reachable. That isn't a sign nothing changed; the timeline just couldn't be read."),
        };

        return new ToolResult<AuditData>(
            Tool: LlmTools.GetChangeTimeline,
            Confidence: confidence,
            Subject: new ResultRef(ResourceKind.Audit, subject),
            Summary: summary,
            Data: data);
    }

    /// <summary>
    /// Authors the grounding sentence: a count-by-type breakdown (most frequent first, ties broken
    /// alphabetically for a stable/deterministic order), plus an honest note when some rows carry no
    /// actor. Capped at the 8 most common types so a diverse window doesn't produce a wall of text —
    /// the full list is always in the card's <see cref="AuditData.Events"/>, never lost.
    /// </summary>
    private static string BuildSummary(
        string? instance, string window, IReadOnlyList<AuditEventRow> events,
        string emptyWording, bool changeFraming = false)
    {
        if (events.Count == 0)
            return emptyWording;

        var groups = events
            .GroupBy(e => e.Type, StringComparer.Ordinal)
            .Select(g => (Type: g.Key, Count: g.Count()))
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Type, StringComparer.Ordinal)
            .ToList();

        const int maxGroups = 8;
        var shown = groups.Take(maxGroups)
            .Select(g => $"{g.Count} {Label(g.Type, g.Count)}");
        var omitted = groups.Count - Math.Min(groups.Count, maxGroups);

        var subject = instance ?? "all servers";
        var noun = changeFraming ? "change" : "event";
        var sentence = $"{events.Count} {noun}{(events.Count == 1 ? "" : "s")} for {subject} in the last " +
            $"{window}: {string.Join(", ", shown)}" + (omitted > 0 ? $", +{omitted} other kind(s)" : "") + ".";

        var unknownActor = events.Count(e => e.Actor is null);
        if (unknownActor > 0)
            sentence += $" Actor is unknown for {unknownActor} of these.";

        return sentence;
    }

    private static string Label(string type, int count) =>
        TypeLabels.TryGetValue(type, out var label) ? (count == 1 ? label.One : label.Many) : type;
}
