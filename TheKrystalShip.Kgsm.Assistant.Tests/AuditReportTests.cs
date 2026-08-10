using FluentAssertions;

using TheKrystalShip.Kgsm.Assistant.Audit;
using TheKrystalShip.Kgsm.Assistant.Envelope;
using TheKrystalShip.Kgsm.Assistant.Ports;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// The pure <c>get_audit_log</c> / <c>get_change_timeline</c> composer: turns a neutral
/// <see cref="EventHistoryReading"/> into an <see cref="AuditData"/> card plus a grounding summary.
/// Covers the honest states (available-with-rows / available-empty / journal-unavailable), the
/// per-event listing the model is grounded on, the never-fabricate rule for a null actor, and the
/// change-timeline's state-changing filter.
/// </summary>
public class AuditReportTests
{
    private static AuditEventRow Row(string type, string? instance = "factorio-test", string? actor = "discord:tester", int minutesAgo = 0) =>
        new($"evt_{type}_{minutesAgo}", DateTimeOffset.UtcNow.AddMinutes(-minutesAgo), type, instance, actor, "assistant");

    /// <summary>The event lines only — the leading frame and the trailing "+N older" note dropped.</summary>
    private static string[] SummaryLines(string summary) =>
        summary.Split('\n').Skip(1).Where(l => !l.StartsWith("(+", StringComparison.Ordinal)).ToArray();

    // ---------------------------------------------------------------- get_audit_log ---------------

    [Fact]
    public void Build_Available_WithRows_ListsEveryEvent_NewestFirst()
    {
        var reading = new EventHistoryReading(AuditReadState.Available, new[]
        {
            Row("instance_started", minutesAgo: 5),
            Row("instance_started", minutesAgo: 65),
            Row("instance_stopped", minutesAgo: 30),
            Row("instance_crashed", minutesAgo: 120),
        });

        var result = AuditReport.Build(reading, "factorio-test", "24h");

        result.Tool.Should().Be(LlmTools.Events);
        result.Confidence.Should().Be(Confidence.Confirmed);
        result.Subject.Should().Be(new ResultRef(ResourceKind.Audit, "factorio-test", "all"));
        result.Data.State.Should().Be(AuditReadState.Available);
        result.Data.Events.Should().HaveCount(4);

        result.Summary.Should().Contain("4 events for factorio-test in the last 24h, newest first");

        // One line per event, in time order — not a tally the model would have to guess back apart.
        var lines = SummaryLines(result.Summary);
        lines.Should().HaveCount(4);
        lines.Select(l => l.Split('—')[1].Trim()).Should().Equal(
            "factorio-test started, by tester",
            "factorio-test stopped, by tester",
            "factorio-test started, by tester",
            "factorio-test crashed, by tester");
    }

    /// <summary>
    /// The point of the listing: each line answers when, which server, what, and who on its own, so
    /// the model can say "tester started factorio-test at 14:05" — which no count can support.
    /// </summary>
    [Fact]
    public void Build_EachLine_CarriesTheTime_TheServer_TheEvent_AndTheActor()
    {
        var at = new DateTimeOffset(2026, 8, 7, 10, 28, 26, TimeSpan.Zero);
        var reading = new EventHistoryReading(AuditReadState.Available, new[]
        {
            new AuditEventRow("evt_1", at, "instance_stopped", "minecraft", "discord:claude", "api"),
        });

        var result = AuditReport.Build(reading, "minecraft", "24h");

        var local = at.ToLocalTime();
        SummaryLines(result.Summary).Should().ContainSingle().Which.Should().Be(
            $"{local:yyyy-MM-dd HH:mm:ss zzz} — minecraft stopped, by claude");
    }

    /// <summary>
    /// Fleet-wide reads mix servers, so the server is on every line rather than in the frame — an
    /// event read against the wrong server is a wrong answer, not a vague one.
    /// </summary>
    [Fact]
    public void Build_FleetWide_NamesTheServerOnEveryLine()
    {
        var reading = new EventHistoryReading(AuditReadState.Available, new[]
        {
            Row("instance_started", instance: "minecraft", minutesAgo: 5),
            Row("instance_backup_created", instance: "romestead", actor: "scheduler", minutesAgo: 30),
        });

        var result = AuditReport.Build(reading, instance: null, "24h");

        result.Summary.Should().Contain("minecraft started, by tester");
        result.Summary.Should().Contain("romestead backed up, by scheduler");
    }

    /// <summary>A host-level event has no instance; it is labelled as such, never blank.</summary>
    [Fact]
    public void Build_HostLevelEvent_IsLabelled_NotLeftServerless()
    {
        var reading = new EventHistoryReading(AuditReadState.Available, new[]
        {
            Row("kgsm_started", instance: null),
        });

        var result = AuditReport.Build(reading, instance: null, "24h");

        result.Summary.Should().Contain("host-level kgsm_started, by tester");
    }

    /// <summary>
    /// A blueprint event carries no instance, so the blueprint name is the only subject it has —
    /// without it the line says something happened to nothing in particular.
    /// </summary>
    [Fact]
    public void Build_BlueprintEvent_NamesTheBlueprint_NotJustHostLevel()
    {
        var reading = new EventHistoryReading(AuditReadState.Available, new[]
        {
            new AuditEventRow("evt_1", DateTimeOffset.UtcNow, "blueprint_updated",
                Instance: null, Actor: "heisen", Origin: "ui", Blueprint: "factorio"),
        });

        var result = AuditReport.Build(reading, instance: null, "24h");

        result.Summary.Should().Contain("blueprint factorio updated, by heisen");
        result.Summary.Should().NotContain("host-level");
    }

    /// <summary>
    /// A window can hold more events than are worth putting in front of the model. The newest are
    /// listed and the rest is declared — a silent trim would let the model treat a partial list as
    /// the whole window.
    /// </summary>
    [Fact]
    public void Build_LongWindow_ListsTheNewest_AndDeclaresTheRemainder()
    {
        var rows = Enumerable.Range(0, 130)
            .Select(i => Row("instance_started", minutesAgo: i))
            .ToArray();

        var result = AuditReport.Build(new EventHistoryReading(AuditReadState.Available, rows), "factorio-test", "24h");

        SummaryLines(result.Summary).Should().HaveCount(100);
        result.Summary.Should().Contain("130 events for factorio-test");
        result.Summary.Should().Contain("(+30 older events in this window, not listed here.)");
        result.Data.Events.Should().HaveCount(130);   // the card still carries all of them
    }

    [Fact]
    public void Build_Available_Empty_IsAnHonestNoEventsMessage_NeverAnError()
    {
        var reading = new EventHistoryReading(AuditReadState.Available, Array.Empty<AuditEventRow>());

        var result = AuditReport.Build(reading, "factorio-test", "1h");

        result.Confidence.Should().Be(Confidence.Confirmed);
        result.Summary.Should().Be("No events recorded for factorio-test in the last 1h.");
        result.Data.Events.Should().BeEmpty();
    }

    [Fact]
    public void Build_MonitorUnavailable_IsHonest_NeverImpliesNothingHappened()
    {
        var reading = new EventHistoryReading(AuditReadState.JournalUnavailable, Array.Empty<AuditEventRow>());

        var result = AuditReport.Build(reading, "factorio-test", "24h");

        result.Confidence.Should().Be(Confidence.Possible);
        result.Summary.Should().Contain("unavailable").And.Contain("isn't a sign nothing happened");
    }

    [Fact]
    public void Build_NoInstance_ScopesToPrimaryHost_AndFleetWording()
    {
        var reading = new EventHistoryReading(AuditReadState.Available, new[] { Row("instance_started", instance: "minecraft") });

        var result = AuditReport.Build(reading, null, "24h");

        result.Subject.Should().Be(new ResultRef(ResourceKind.Audit, "primary", "all"));
        result.Summary.Should().Contain("all servers");
        result.Data.Instance.Should().BeNull();
    }

    [Fact]
    public void Build_NullActor_ReportedAsUnknown_NeverDefaultedToSystem()
    {
        var reading = new EventHistoryReading(AuditReadState.Available, new[]
        {
            Row("instance_started", actor: null),
        });

        var result = AuditReport.Build(reading, "factorio-test", "24h");

        result.Data.Events[0].Actor.Should().BeNull();
        result.Summary.Should().Contain("factorio-test started, actor not recorded");
        result.Summary.Should().NotContain("system");
    }

    /// <summary>
    /// A supervisor acting on its own has no human answer to "who did it", so the provider stays.
    /// Rendering <c>system:watchdog</c> as a bare name would offer one.
    /// </summary>
    [Fact]
    public void Build_SystemActor_KeepsTheProvider_SoItIsNotReadAsAPerson()
    {
        var reading = new EventHistoryReading(AuditReadState.Available, new[]
        {
            Row("instance_started", actor: "system:watchdog"),
        });

        var result = AuditReport.Build(reading, "factorio-test", "24h");

        result.Summary.Should().Contain("by watchdog (system)");
    }

    /// <summary>
    /// Attribution is per event, so a part-attributed window names who did what and says which ones
    /// nobody is recorded for — neither half smooths over the other.
    /// </summary>
    [Fact]
    public void Build_MixedAttribution_AttributesPerEvent_NotAcrossTheWindow()
    {
        var reading = new EventHistoryReading(AuditReadState.Available, new[]
        {
            Row("instance_started", actor: "discord:claude", minutesAgo: 5),
            Row("instance_stopped", actor: null, minutesAgo: 10),
        });

        var result = AuditReport.Build(reading, "factorio-test", "24h");

        result.Summary.Should().Contain("factorio-test started, by claude");
        result.Summary.Should().Contain("factorio-test stopped, actor not recorded");
    }

    /// <summary>The change timeline grounds "who changed it" the same way.</summary>
    [Fact]
    public void BuildChangeTimeline_AlsoNamesActors()
    {
        var reading = new EventHistoryReading(AuditReadState.Available, new[]
        {
            Row("instance_updated", actor: "discord:claude"),
        });

        var result = AuditReport.BuildChangeTimeline(reading, "factorio-test", "7d");

        result.Summary.Should().Contain("factorio-test updated, by claude");
    }

    [Fact]
    public void Build_UnknownEventType_FallsBackToRawTypeString_NeverGuessedGrammar()
    {
        var reading = new EventHistoryReading(AuditReadState.Available, new[] { Row("instance_relocated") });

        var result = AuditReport.Build(reading, "factorio-test", "24h");

        result.Summary.Should().Contain("factorio-test instance_relocated,");
    }

    // ------------------------------------------------------------ get_change_timeline -------------

    [Fact]
    public void BuildChangeTimeline_FiltersOutRoutineAndPlayerEvents_KeepsOnlyStateChanges()
    {
        var reading = new EventHistoryReading(AuditReadState.Available, new[]
        {
            Row("instance_started"),
            Row("instance_stopped"),
            Row("instance_crashed"),
            Row("instance_player_joined"),
            Row("instance_player_left"),
            Row("instance_installed"),
            Row("instance_updated"),
            Row("instance_version_updated"),
            Row("instance_backup_created"),
            Row("instance_ports_opened"),
            Row("instance_ports_closed"),
            Row("instance_uninstalled"),
        });

        var result = AuditReport.BuildChangeTimeline(reading, "factorio-test", "7d");

        result.Tool.Should().Be(LlmTools.Events);
        var keptTypes = result.Data.Events.Select(e => e.Type).ToHashSet();
        keptTypes.Should().BeEquivalentTo(new[]
        {
            "instance_installed", "instance_updated", "instance_version_updated",
            "instance_backup_created", "instance_ports_opened", "instance_ports_closed",
            "instance_uninstalled",
        });
        keptTypes.Should().NotContain("instance_started").And.NotContain("instance_stopped")
            .And.NotContain("instance_crashed").And.NotContain("instance_player_joined")
            .And.NotContain("instance_player_left");
    }

    [Fact]
    public void BuildChangeTimeline_Empty_ExplainsWhatCountsAsAChange()
    {
        var reading = new EventHistoryReading(AuditReadState.Available, new[] { Row("instance_started") });

        var result = AuditReport.BuildChangeTimeline(reading, "factorio-test", "7d");

        result.Data.Events.Should().BeEmpty();
        result.Summary.Should().Contain("No changes recorded for factorio-test in the last 7d");
        result.Summary.Should().Contain("routine starts/stops and player activity don't");
    }

    [Fact]
    public void BuildChangeTimeline_MonitorUnavailable_IsHonest()
    {
        var reading = new EventHistoryReading(AuditReadState.JournalUnavailable, Array.Empty<AuditEventRow>());

        var result = AuditReport.BuildChangeTimeline(reading, "factorio-test", "7d");

        result.Confidence.Should().Be(Confidence.Possible);
        result.Summary.Should().Contain("unavailable").And.Contain("isn't a sign nothing changed");
    }

    [Fact]
    public void BuildChangeTimeline_Summary_UsesChangeFraming_NotEventFraming()
    {
        var reading = new EventHistoryReading(AuditReadState.Available, new[] { Row("instance_installed") });

        var result = AuditReport.BuildChangeTimeline(reading, "factorio-test", "7d");

        result.Summary.Should().Contain("1 change for factorio-test");
        result.Summary.Should().NotContain("1 event for");
    }

    // ------------------------------------------------------------------- AuditWindow ---------------

    [Fact]
    public void AuditWindow_Resolve_RecognizedToken_UsesItsSpan()
    {
        var now = DateTimeOffset.UtcNow;
        var (label, sinceMs) = AuditWindow.Resolve("1h", AuditWindow.DefaultAuditWindow, now);

        label.Should().Be("1h");
        sinceMs.Should().Be(now.AddHours(-1).ToUnixTimeMilliseconds());
    }

    [Fact]
    public void AuditWindow_Resolve_Omitted_UsesTheCallersDefault()
    {
        var now = DateTimeOffset.UtcNow;
        var (label, sinceMs) = AuditWindow.Resolve(null, AuditWindow.DefaultChangeRange, now);

        label.Should().Be("7d");
        sinceMs.Should().Be(now.AddDays(-7).ToUnixTimeMilliseconds());
    }

    [Fact]
    public void AuditWindow_Resolve_UnrecognizedToken_HonestlyFallsBackToDefault_NeverErrors()
    {
        var now = DateTimeOffset.UtcNow;
        var (label, sinceMs) = AuditWindow.Resolve("last tuesday", AuditWindow.DefaultAuditWindow, now);

        label.Should().Be(AuditWindow.DefaultAuditWindow);
        sinceMs.Should().Be(now.AddHours(-24).ToUnixTimeMilliseconds());
    }
}
