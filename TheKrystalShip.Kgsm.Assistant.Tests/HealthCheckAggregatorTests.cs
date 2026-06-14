using FluentAssertions;

using TheKrystalShip.Kgsm.Assistant.Health;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Kgsm.Assistant.Envelope;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// The deterministic health synthesis (toolbox-plan §3.4/§3.6) is a pure function, so
/// these run without mocks. They pin the load-bearing rule (§D5): a deliberately
/// stopped server is never a failure, and an unreadable source <c>Skip</c>s rather than
/// fabricating a pass.
/// </summary>
public class HealthCheckAggregatorTests
{
    /// <summary>A healthy running instance: running, no error logs, up to date, disk 26%.</summary>
    private static InstanceHealthSnapshot Healthy(
        bool running = true,
        IReadOnlyList<string>? logs = null,
        bool? updatesAvailable = false,
        string? current = "1.0.0",
        string? latest = null,
        HostDisk? disk = null,
        bool hasDisk = true,
        string? diskReason = null) =>
        new(
            Running: running,
            RecentLogLines: logs ?? Array.Empty<string>(),
            UpdatesAvailable: updatesAvailable,
            CurrentVersion: current,
            LatestVersion: latest,
            // hasDisk:false models "host read failed" — distinct from a present-but-default disk.
            HostDisk: hasDisk ? (disk ?? new HostDisk(26, "916G", "649G")) : null,
            HostDiskUnavailableReason: diskReason);

    private static HealthCheck Check(ToolResult<HealthData> r, string name) =>
        r.Data.Checks.Single(c => c.Name == name);

    [Fact]
    public void Healthy_RunningInstance_AllPass()
    {
        var r = HealthCheckAggregator.Run(Healthy(), "minecraft");

        r.Data.Overall.Should().Be(CheckState.Pass);
        r.Data.Total.Should().Be(4);
        r.Data.Passed.Should().Be(4);
        r.Data.Skipped.Should().Be(0);
        r.Tool.Should().Be(LlmTools.RunHealthCheck);
        r.Confidence.Should().Be(Confidence.Confirmed);
        r.Subject.Should().Be(new ResultRef(ResourceKind.Server, "minecraft"));
        r.Summary.Should().Contain("minecraft").And.Contain("healthy");
    }

    [Fact]
    public void Stopped_IsNeverFailure_AndSkipsLogScan()
    {
        // The §D5 proof: stateless KGSM can't know stopped is a crash, so it must not fail.
        var r = HealthCheckAggregator.Run(Healthy(running: false), "factorio-test");

        Check(r, "liveness").State.Should().Be(CheckState.Pass);
        Check(r, "liveness").Severity.Should().Be(Severity.Info);
        Check(r, "logs").State.Should().Be(CheckState.Skip);
        r.Data.Overall.Should().NotBe(CheckState.Fail);
        r.Data.Overall.Should().Be(CheckState.Pass);
        r.Data.Skipped.Should().Be(1);
        r.Summary.Should().Contain("stopped").And.Contain("skipped");
    }

    [Fact]
    public void RunningWithErrorLogs_WarnsAndSamples()
    {
        var logs = new[]
        {
            "2026-06-14 10:00:00 INFO  server started",
            "2026-06-14 10:01:00 ERROR failed to bind port 25565",
            "2026-06-14 10:02:00 normal line",
        };

        var r = HealthCheckAggregator.Run(Healthy(logs: logs), "minecraft");

        Check(r, "logs").State.Should().Be(CheckState.Warn);
        Check(r, "logs").Detail.Should().Contain("1 error line").And.Contain("failed to bind");
        r.Data.Overall.Should().Be(CheckState.Warn);
        r.Summary.Should().Contain("warnings");
    }

    [Fact]
    public void UpdateAvailable_WarnsWithVersions()
    {
        var r = HealthCheckAggregator.Run(
            Healthy(updatesAvailable: true, current: "1.20.1", latest: "1.20.4"), "minecraft");

        var updates = Check(r, "updates");
        updates.State.Should().Be(CheckState.Warn);
        updates.Severity.Should().Be(Severity.Update);
        updates.Detail.Should().Contain("1.20.1").And.Contain("1.20.4");
        r.Summary.Should().Contain("Update available");
    }

    [Fact]
    public void UpdateStatusUnknown_Skips_NeverGuesses()
    {
        var r = HealthCheckAggregator.Run(Healthy(updatesAvailable: null), "minecraft");

        Check(r, "updates").State.Should().Be(CheckState.Skip);
        r.Data.Skipped.Should().Be(1);
        // A null update status must not drag the verdict down.
        r.Data.Overall.Should().Be(CheckState.Pass);
    }

    [Theory]
    [InlineData(85, CheckState.Warn)]
    [InlineData(90, CheckState.Warn)]
    [InlineData(95, CheckState.Fail)]
    [InlineData(99, CheckState.Fail)]
    public void Disk_ThresholdsHeadroom(int usedPercent, CheckState expected)
    {
        var r = HealthCheckAggregator.Run(
            Healthy(disk: new HostDisk(usedPercent, "916G", "10G")), "minecraft");

        Check(r, "disk").State.Should().Be(expected);
        r.Data.Overall.Should().Be(expected); // disk is the only non-pass check
    }

    [Fact]
    public void Disk_Unavailable_Skips_WithReason_NeverFakesZero()
    {
        var r = HealthCheckAggregator.Run(
            Healthy(hasDisk: false, diskReason: "monitor offline"), "minecraft");

        var disk = Check(r, "disk");
        disk.State.Should().Be(CheckState.Skip);
        disk.Detail.Should().Contain("monitor offline");
        disk.Detail.Should().NotContain("0%");
        r.Data.Skipped.Should().Be(1);
        r.Data.Overall.Should().Be(CheckState.Pass);
    }

    [Fact]
    public void Disk_PercentUnparsed_Skips()
    {
        var r = HealthCheckAggregator.Run(
            Healthy(disk: new HostDisk(null, "916G", "649G")), "minecraft");

        Check(r, "disk").State.Should().Be(CheckState.Skip);
    }

    [Fact]
    public void Overall_IsWorstNonSkipCheck()
    {
        // Running + error logs (warn) + disk critically full (fail) → overall fail.
        var logs = new[] { "FATAL out of memory" };
        var r = HealthCheckAggregator.Run(
            Healthy(logs: logs, disk: new HostDisk(98, "916G", "1G")), "minecraft");

        Check(r, "logs").State.Should().Be(CheckState.Warn);
        Check(r, "disk").State.Should().Be(CheckState.Fail);
        r.Data.Overall.Should().Be(CheckState.Fail);
        r.Summary.Should().Contain("problems");
    }
}
