using FluentAssertions;

using TheKrystalShip.Kgsm.Assistant.Health;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Kgsm.Assistant.Envelope;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// The deterministic health synthesis is a pure function, so these run without mocks.
/// They pin the load-bearing rule: a deliberately stopped server is never a failure, and
/// an unreadable source <c>Skip</c>s rather than fabricating a pass.
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
        string? diskReason = null,
        bool? portsReachable = true,
        string? portsDetail = null) =>
        new(
            Running: running,
            // A healthy running server HAS clean log output; the logs check now Skips on an
            // empty read (honest "nothing to scan"), so the baseline must carry a real line.
            RecentLogLines: logs ?? new[] { "2026-06-14 10:00:00 INFO server running" },
            UpdatesAvailable: updatesAvailable,
            CurrentVersion: current,
            LatestVersion: latest,
            // hasDisk:false models "host read failed" — distinct from a present-but-default disk.
            HostDisk: hasDisk ? (disk ?? new HostDisk(26, "916G", "649G")) : null,
            HostDiskUnavailableReason: diskReason,
            // A healthy running server's configured ports are active; a null models "not probed" (skip).
            PortsReachable: portsReachable,
            PortsDetail: portsDetail);

    private static HealthCheck Check(ToolResult<HealthData> r, string name) =>
        r.Data.Checks.Single(c => c.Name == name);

    [Fact]
    public void Healthy_RunningInstance_AllPass()
    {
        var r = HealthCheckAggregator.Run(Healthy(), "minecraft");

        r.Data.Overall.Should().Be(CheckState.Pass);
        r.Data.Total.Should().Be(5);
        r.Data.Passed.Should().Be(5);
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
        // Ports also skip on a stopped server (nothing is bound) — a second honest skip.
        Check(r, "ports").State.Should().Be(CheckState.Skip);
        r.Data.Overall.Should().NotBe(CheckState.Fail);
        r.Data.Overall.Should().Be(CheckState.Pass);
        r.Data.Skipped.Should().Be(2);
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
    public void RunningWithNoLogs_SkipsHonestly_NeverFakesClean()
    {
        // Honesty: a running instance with NO recent log lines has nothing to scan, so the
        // check must Skip ("couldn't read") — asserting "no errors" from zero evidence would
        // be a fabricated clean bill (the kgsm recent_logs=[] bug this guards against).
        var r = HealthCheckAggregator.Run(Healthy(logs: Array.Empty<string>()), "factorio-test");

        var logs = Check(r, "logs");
        logs.State.Should().Be(CheckState.Skip);
        logs.State.Should().NotBe(CheckState.Pass);
        logs.Detail.Should().Contain("No recent log output");
        r.Data.Overall.Should().Be(CheckState.Pass);   // a skip never fails the overall
        r.Data.Skipped.Should().Be(1);
        r.Data.Passed.Should().Be(4);                  // liveness + updates + disk + ports
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
    public void Ports_ReachableWhileRunning_Passes()
    {
        var r = HealthCheckAggregator.Run(Healthy(portsReachable: true), "factorio-test");

        var ports = Check(r, "ports");
        ports.State.Should().Be(CheckState.Pass);
        ports.Severity.Should().Be(Severity.Success);
        r.Data.Overall.Should().Be(CheckState.Pass);
    }

    [Fact]
    public void Ports_UnreachableWhileRunning_Warns_NotFail()
    {
        // A running server whose ports aren't bound is a warning (it may still be starting), never a
        // hard fail — the honest middle ground.
        var r = HealthCheckAggregator.Run(Healthy(portsReachable: false), "factorio-test");

        var ports = Check(r, "ports");
        ports.State.Should().Be(CheckState.Warn);
        ports.State.Should().NotBe(CheckState.Fail);
        r.Data.Overall.Should().Be(CheckState.Warn);
    }

    [Fact]
    public void Ports_NotProbed_Skips_WithReason_NeverFabricatesPass()
    {
        // null reachability (no ports configured / probe failed) must Skip, never assert reachable.
        var r = HealthCheckAggregator.Run(
            Healthy(portsReachable: null, portsDetail: "no ports configured"), "factorio-test");

        var ports = Check(r, "ports");
        ports.State.Should().Be(CheckState.Skip);
        ports.State.Should().NotBe(CheckState.Pass);
        ports.Detail.Should().Contain("no ports configured");
        r.Data.Overall.Should().Be(CheckState.Pass); // a skip never fails the overall
    }

    [Fact]
    public void Ports_StoppedServer_Skips_WithoutProbing()
    {
        // Even with a stale true, a stopped server's ports check skips (binding is meaningless stopped).
        var r = HealthCheckAggregator.Run(Healthy(running: false, portsReachable: true), "factorio-test");

        Check(r, "ports").State.Should().Be(CheckState.Skip);
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
