using FluentAssertions;

using TheKrystalShip.Kgsm.Assistant.Audit;
using TheKrystalShip.Kgsm.Assistant.Envelope;
using TheKrystalShip.Kgsm.Assistant.Ports;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// The pure <c>get_audit_log</c> / <c>get_change_timeline</c> composer: turns a neutral
/// <see cref="EventHistoryReading"/> into an <see cref="AuditData"/> card plus a grounding summary.
/// Covers the honest states (available-with-rows / available-empty / monitor-unavailable), the
/// deterministic type-count summary, the never-fabricate rule for a null actor, and the
/// change-timeline's state-changing filter.
/// </summary>
public class AuditReportTests
{
    private static AuditEventRow Row(string type, string? instance = "factorio-test", string? actor = "discord:tester", int minutesAgo = 0) =>
        new($"evt_{type}_{minutesAgo}", DateTimeOffset.UtcNow.AddMinutes(-minutesAgo), type, instance, actor, "assistant");

    // ---------------------------------------------------------------- get_audit_log ---------------

    [Fact]
    public void Build_Available_WithRows_CountsByType_MostFrequentFirst()
    {
        var reading = new EventHistoryReading(AuditReadState.Available, new[]
        {
            Row("instance_started", minutesAgo: 5),
            Row("instance_started", minutesAgo: 65),
            Row("instance_stopped", minutesAgo: 30),
            Row("instance_crashed", minutesAgo: 120),
        });

        var result = AuditReport.Build(reading, "factorio-test", "24h");

        result.Tool.Should().Be(LlmTools.GetAuditLog);
        result.Confidence.Should().Be(Confidence.Confirmed);
        result.Subject.Should().Be(new ResultRef(ResourceKind.Audit, "factorio-test"));
        result.Data.State.Should().Be(AuditReadState.Available);
        result.Data.Events.Should().HaveCount(4);
        // 2 starts leads; ties among the rest broken alphabetically (crash before stop).
        result.Summary.Should().Contain("4 events for factorio-test in the last 24h:")
            .And.Contain("2 starts").And.Contain("1 crash").And.Contain("1 stop");
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

        result.Subject.Should().Be(new ResultRef(ResourceKind.Audit, "primary"));
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
        result.Summary.Should().Contain("Actor is unknown for 1 of these");
        result.Summary.Should().NotContain("system");
    }

    /// <summary>
    /// The events carry the actor, so the summary must say who — a model handed only a count of
    /// events answers "the log does not record who" about a log that does.
    /// </summary>
    [Fact]
    public void Build_NamesTheActorsItHas()
    {
        var reading = new EventHistoryReading(AuditReadState.Available, new[]
        {
            Row("instance_started", actor: "discord:claude"),
            Row("instance_stopped", actor: "discord:claude"),
            Row("instance_backup_created", actor: "scheduler"),
        });

        var result = AuditReport.Build(reading, "factorio-test", "24h");

        result.Summary.Should().Contain("By: claude (2)");
        result.Summary.Should().Contain("scheduler (1)");
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

        result.Summary.Should().Contain("watchdog (system) (1)");
    }

    /// <summary>
    /// Naming the known actors must not soften the unknown ones — a window that is part-attributed
    /// says both things.
    /// </summary>
    [Fact]
    public void Build_MixedAttribution_NamesTheKnown_AndStillFlagsTheUnknown()
    {
        var reading = new EventHistoryReading(AuditReadState.Available, new[]
        {
            Row("instance_started", actor: "discord:claude"),
            Row("instance_stopped", actor: null),
        });

        var result = AuditReport.Build(reading, "factorio-test", "24h");

        result.Summary.Should().Contain("By: claude (1)");
        result.Summary.Should().Contain("Actor is unknown for 1 of these");
    }

    /// <summary>Deterministic wording: most events first, ties alphabetical.</summary>
    [Fact]
    public void Build_ActorOrder_IsCountThenAlphabetical()
    {
        var reading = new EventHistoryReading(AuditReadState.Available, new[]
        {
            Row("instance_started", actor: "discord:zoe"),
            Row("instance_stopped", actor: "discord:adam"),
            Row("instance_started", actor: "discord:mia"),
            Row("instance_stopped", actor: "discord:mia"),
        });

        var result = AuditReport.Build(reading, "factorio-test", "24h");

        result.Summary.Should().Contain("By: mia (2), adam (1), zoe (1)");
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

        result.Summary.Should().Contain("By: claude (1)");
    }

    [Fact]
    public void Build_UnknownEventType_FallsBackToRawTypeString_NeverGuessedGrammar()
    {
        var reading = new EventHistoryReading(AuditReadState.Available, new[] { Row("instance_relocated") });

        var result = AuditReport.Build(reading, "factorio-test", "24h");

        result.Summary.Should().Contain("1 instance_relocated");
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

        result.Tool.Should().Be(LlmTools.GetChangeTimeline);
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
