using System.Globalization;

using TheKrystalShip.Kgsm.Assistant.Envelope;
using TheKrystalShip.Kgsm.Assistant.Ports;

namespace TheKrystalShip.Kgsm.Assistant.Audit;

/// <summary>
/// Resolves the model-supplied <c>window</c>/<c>range</c> vocabulary (<c>1h</c>/<c>24h</c>/<c>7d</c>/
/// <c>30d</c> — the same set <c>get_performance</c> uses) into a <c>since</c> unix-ms bound for the
/// the engine's event journal. An unrecognized or omitted value is not an error — it honestly
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
/// The deterministic <c>events</c> composer (mirrors
/// <see cref="Metrics.PerformanceReport"/>): turns a neutral <see cref="Ports.EventHistoryReading"/>
/// into an <see cref="AuditData"/> card plus a model-grounding <see cref="ToolResult{TData}.Summary"/>.
/// Pure and I/O-free (the socket fetch happens in the port impl), so it's unit-testable without mocks
/// and is the single home for how an event window is worded.
/// <para>
/// The summary is the event list itself — one line per event, newest first, each carrying when,
/// which server, what, and who. The model never sees <see cref="AuditData"/>, so anything the
/// summary condenses is simply gone: it could not say when a server was started or who stopped it,
/// only how many times something of that kind occurred.
/// </para>
/// <para>
/// Honesty rules baked in: a <see cref="AuditReadState.JournalUnavailable"/> read is an honest
/// "couldn't read" — explicitly NOT a claim that nothing happened; an
/// <see cref="AuditReadState.Available"/> read with zero rows is a real, measured "no events
/// recorded"; an event with no <see cref="AuditEventRow.Actor"/> says so on its own line, never
/// defaulted to a placeholder like "system" (the never-fabricate rule); and a window holding more
/// events than are listed declares the remainder as a count rather than trimming in silence.
/// </para>
/// </summary>
public static class AuditReport
{
    /// <summary>
    /// The state-changing subset the <c>changes</c> scope filters to — durable changes to a
    /// server's existence, version, or exposed configuration. Deliberately EXCLUDES:
    /// <c>instance_started</c>/<c>instance_stopped</c> (routine run-state flips, not a change to the
    /// server itself — <c>events</c>/<c>server_info</c> are the right tools for those),
    /// <c>instance_crashed</c> (a fault/operational event for root-cause tracing, not a deliberate
    /// change), and <c>instance_player_joined</c>/<c>instance_player_left</c> (player activity, not
    /// server state). An event type this host has never seen (a future kgsm event) is honestly left
    /// OUT of the timeline rather than guessed at — it still shows up in the unfiltered scope.
    /// </summary>
    public static readonly IReadOnlySet<string> ChangeEventTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "server.installed",
        "server.uninstalled",
        "server.update.completed",
        "server.updated",
        "backup.created",
        "network.ports.opened",
        "network.ports.closed",
    };

    /// <summary>Friendly labels for the raw kgsm event-type vocabulary, so a listed event reads as
    /// something that happened rather than as an identifier. An event type not in this map (a
    /// future/unknown kgsm event) falls back to its raw type string — honest, never a guessed
    /// wording.</summary>
    private static readonly IReadOnlyDictionary<string, string> TypeLabels =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["server.started"] = "started",
            ["server.stopped"] = "stopped",
            ["server.restarted"] = "restarted",
            ["server.crashed"] = "crashed",
            ["server.ready"] = "became ready",
            ["server.install.created"] = "created",
            ["server.uninstall.removed"] = "removed",
            ["server.update.completed"] = "updated",
            ["server.installed"] = "installed",
            ["server.uninstalled"] = "uninstalled",
            ["server.deploy.completed"] = "deployed",
            ["server.download.completed"] = "downloaded",
            ["server.updated"] = "version updated",
            ["backup.created"] = "backed up",
            ["backup.restored"] = "backup restored",
            ["config.changed"] = "config changed",
            ["console.input.sent"] = "console input sent",
            ["network.ports.opened"] = "ports opened",
            ["network.ports.closed"] = "ports closed",
            ["network.upnp.opened"] = "UPnP forward opened",
            ["network.upnp.closed"] = "UPnP forward closed",
            ["network.upnp.reasserted"] = "UPnP forward restored after the router dropped it",
            ["player.joined"] = "player joined",
            ["player.left"] = "player left",
            ["player.kicked"] = "player kicked",
            ["player.banned"] = "player banned",
            ["player.unbanned"] = "player unbanned",
            ["server.stop.started"] = "stop began",
            ["server.stop.finished"] = "stop finished",
            ["server.install.started"] = "install began",
            ["server.install.finished"] = "install finished",
            ["server.uninstall.started"] = "uninstall began",
            ["server.uninstall.finished"] = "uninstall finished",
            ["server.update.started"] = "update began",
            ["server.update.finished"] = "update finished",
            ["server.download.started"] = "download began",
            ["server.download.finished"] = "download finished",
            ["server.deploy.started"] = "deploy began",
            ["server.deploy.finished"] = "deploy finished",
            ["server.install.directories_created"] = "directories created",
            ["server.uninstall.directories_removed"] = "directories removed",
            ["server.install.files_created"] = "files created",
            ["server.uninstall.files_removed"] = "files removed",
            // A blueprint event names its subject as "blueprint <name>", so the label stays bare.
            ["blueprint.created"] = "created",
            ["blueprint.updated"] = "updated",
            ["blueprint.removed"] = "removed",
        };

    /// <summary>
    /// Builds the unfiltered <c>events</c> result: every event in the window, unfiltered by type.
    /// </summary>
    public static ToolResult<AuditData> Build(EventHistoryReading reading, string? instance, string window)
    {
        var subject = instance ?? "primary";
        var events = NewestFirst(reading.Events);
        var data = new AuditData(instance, window, reading.State, events);

        var (confidence, summary) = reading.State switch
        {
            AuditReadState.Available => (Confidence.Confirmed, BuildSummary(instance, window, events,
                emptyWording: $"No events recorded for {(instance ?? "any server")} in the last {window}.")),
            _ => (Confidence.Possible,
                $"Event history for {(instance ?? "this host")} is unavailable right now — the engine's " +
                "event journal couldn't be read. That isn't a sign nothing happened; the events just couldn't be read."),
        };

        return new ToolResult<AuditData>(
            Tool: ResultCardKinds.AuditLog,
            Confidence: confidence,
            Subject: new ResultRef(ResourceKind.Audit, subject, "all"),
            Summary: summary,
            Data: data);
    }
    /// <summary>
    /// Orders a read newest-first, breaking a same-timestamp tie by id descending. The port already
    /// returns rows in this order; ordering here is what makes the card and the listing say the same
    /// thing in the same sequence, so the model's "the most recent event was X" and the card's top
    /// row cannot disagree.
    /// </summary>
    private static IReadOnlyList<AuditEventRow> NewestFirst(IReadOnlyList<AuditEventRow> events) =>
        events
            .OrderByDescending(e => e.Ts)
            .ThenByDescending(e => e.Id, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// The most events listed to the model in one read. The window can legitimately hold more (the
    /// port fetches up to a few hundred), and a list that long crowds out the conversation it is
    /// meant to inform, so the newest <see cref="MaxListedEvents"/> are listed and the remainder is
    /// declared as a count rather than dropped silently. The card's
    /// <see cref="AuditData.Events"/> always carries every row.
    /// </summary>
    private const int MaxListedEvents = 100;

    /// <summary>
    /// Authors the grounding text: a one-line frame (how many, which server, which window), then one
    /// line per event, newest first. Each line carries when it happened, which server it happened
    /// to, what happened, and who did it — the four things "what happened here?" is really asking,
    /// which no aggregate of them can answer.
    /// </summary>
    /// <remarks>
    /// The model sees only this text — <see cref="AuditData"/> is surface-only — so an aggregate
    /// here means the model cannot say a single concrete thing about a single event no matter what
    /// it is asked. Counts are recoverable from a list; a list is not recoverable from counts.
    /// </remarks>
    private static string BuildSummary(
        string? instance, string window, IReadOnlyList<AuditEventRow> events,
        string emptyWording, bool changeFraming = false)
    {
        if (events.Count == 0)
            return emptyWording;

        var subject = instance ?? "all servers";
        var noun = changeFraming ? "change" : "event";
        var header = $"{events.Count} {noun}{(events.Count == 1 ? "" : "s")} for {subject} in the last " +
            $"{window}, newest first, times host-local:";

        var listed = string.Join("\n", events.Take(MaxListedEvents).Select(Describe));

        var omitted = events.Count - Math.Min(events.Count, MaxListedEvents);
        var tail = omitted > 0
            ? $"\n(+{omitted} older {noun}{(omitted == 1 ? "" : "s")} in this window, not listed here.)"
            : "";

        return header + "\n" + listed + tail;
    }

    /// <summary>
    /// Renders one event as a line the model can quote or rephrase without joining anything back
    /// together: every line is self-contained, so an event cannot be read against the wrong server
    /// or the wrong actor.
    /// <para>
    /// The timestamp is the host's local time with its UTC offset spelled out on every line, rather
    /// than a zone named once up top: an offset stated per line stays true across a window that
    /// crosses a DST boundary, and an operator reading it against their own wall clock never has to
    /// guess which frame it is in.
    /// </para>
    /// </summary>
    private static string Describe(AuditEventRow e)
    {
        var when = e.Ts.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
        var subject = e.Instance is { Length: > 0 } name ? name
            : e.Blueprint is { Length: > 0 } blueprint ? $"blueprint {blueprint}"
            : "host-level";
        var what = $"{subject} {Label(e.Type)}";
        var who = e.Actor is { Length: > 0 } actor
            ? $"by {DescribeActor(actor)}"
            : "actor not recorded";

        return $"{when} — {what}, {who}";
    }

    /// <summary>
    /// Renders one actor for the summary. An actor arrives as <c>provider:name</c> (or a bare name
    /// for an OS user), and the provider is kept rather than stripped because <c>system</c> is not a
    /// person: an event a supervisor performed on its own has no human answer to "who did it", and
    /// flattening it to a bare name would offer one.
    /// </summary>
    private static string DescribeActor(string actor)
    {
        int split = actor.IndexOf(':');
        if (split <= 0 || split == actor.Length - 1)
            return actor;

        var provider = actor[..split];
        var name = actor[(split + 1)..];

        return string.Equals(provider, "system", StringComparison.Ordinal)
            ? $"{name} (system)"
            : name;
    }

    private static string Label(string type) =>
        TypeLabels.TryGetValue(type, out var label) ? label : type;
}
