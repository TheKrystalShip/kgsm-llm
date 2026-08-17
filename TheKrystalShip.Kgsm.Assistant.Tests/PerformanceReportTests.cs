using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using FluentAssertions;

using TheKrystalShip.Kgsm.Assistant.Envelope;
using TheKrystalShip.Kgsm.Assistant.Metrics;
using TheKrystalShip.Kgsm.Assistant.Ports;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// The pure <c>get_performance</c> composer: turns a neutral <see cref="ServerMetricsReading"/> into a
/// grounding summary + card. Covers the three states (live values / not-running / monitor-unavailable),
/// the honest omission of a null (unmeasured) axis from the summary (never shown as 0), and the byte/bps
/// formatting boundaries.
/// </summary>
public class PerformanceReportTests
{
    private static ServerMetricsReading Live(
        double? cpu = 42.5, long? mem = 1_073_741_824,
        long? rx = 1024, long? tx = 2048, long? ior = 4096, long? iow = 512,
        long? disk = 5_242_880, int? pids = 7) =>
        new(PerformanceState.Live, cpu, mem, rx, tx, ior, iow, disk, pids);

    [Fact]
    public void Live_ReportsMeasuredCpuAndMemory_AsAConfirmedMetricsCard()
    {
        var result = PerformanceReport.Build(Live(), "minecraft");

        result.Tool.Should().Be(ResultCardKinds.Performance);
        result.Confidence.Should().Be(Confidence.Confirmed);            // deterministic read of measured facts
        result.Subject.Should().Be(new ResultRef(ResourceKind.Metrics, "minecraft"));
        result.Summary.Should().Contain("minecraft").And.Contain("42.5%").And.Contain("1 GB");
        result.Data.State.Should().Be(PerformanceState.Live);
        result.Data.Instance.Should().Be("minecraft");
        result.Data.CpuPctCore.Should().Be(42.5);
        result.Data.MemBytes.Should().Be(1_073_741_824);
        result.Data.Pids.Should().Be(7);
    }

    [Fact]
    public void Live_SingleProcess_UsesSingularWording()
    {
        var result = PerformanceReport.Build(Live(pids: 1), "terraria");

        result.Summary.Should().Contain("1 process.").And.NotContain("1 processes");
    }

    [Fact]
    public void Live_NullNetwork_OmitsTheNetworkClause_AndSaysSoHonestly_NeverZero()
    {
        // Network entirely unmeasured (null) — the clause is dropped, and an honest note is added
        // instead of narrating "0 B/s" (the never-fabricate rule).
        var result = PerformanceReport.Build(Live(rx: null, tx: null), "minecraft");

        result.Summary.Should().NotContain("Receiving");
        result.Summary.Should().Contain("Network throughput isn't being measured");
        // A missing meter must not surface as zero traffic.
        result.Summary.Should().NotContain("0 B/s, sending");
    }

    [Fact]
    public void Live_NullDiskIo_OmitsTheDiskIoClause()
    {
        var result = PerformanceReport.Build(Live(ior: null, iow: null), "minecraft");

        result.Summary.Should().NotContain("Disk I/O");
    }

    [Fact]
    public void Live_MeasuredNetwork_ReportsBothDirectionsAsRates()
    {
        var result = PerformanceReport.Build(Live(rx: 1024, tx: 2_097_152), "minecraft");

        result.Summary.Should().Contain("Receiving 1 KB/s").And.Contain("sending 2 MB/s");
        result.Summary.Should().NotContain("isn't being measured");
    }

    [Fact]
    public void NotRunning_SaysNoLiveMetrics_WithNullValues()
    {
        var reading = new ServerMetricsReading(
            PerformanceState.NotRunning, null, null, null, null, null, null, null, null);

        var result = PerformanceReport.Build(reading, "minecraft");

        result.Summary.Should().Contain("minecraft").And.Contain("isn't running");
        result.Confidence.Should().Be(Confidence.Confirmed);
        result.Data.State.Should().Be(PerformanceState.NotRunning);
        result.Data.CpuPctCore.Should().BeNull();
        result.Data.MemBytes.Should().BeNull();
    }

    [Fact]
    public void MonitorUnavailable_IsHonestCouldntRead_NotEvidenceOfIdle()
    {
        var reading = new ServerMetricsReading(
            PerformanceState.MonitorUnavailable, null, null, null, null, null, null, null, null);

        var result = PerformanceReport.Build(reading, "minecraft");

        result.Summary.Should().Contain("unavailable")
            .And.Contain("isn't a sign the server is idle")
            .And.Contain("couldn't be read");
        result.Confidence.Should().Be(Confidence.Possible);     // not measured → only a possible conclusion
        result.Data.State.Should().Be(PerformanceState.MonitorUnavailable);
    }

    [Theory]
    [InlineData(512L, "512 B")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1_572_864L, "1.5 MB")]
    [InlineData(1_073_741_824L, "1 GB")]
    public void Live_FormatsMemorySizeAcrossUnitBoundaries(long mem, string expected)
    {
        var result = PerformanceReport.Build(Live(mem: mem), "x");

        result.Summary.Should().Contain($"{expected} of memory");
    }

    [Fact]
    public void Live_FormatsRatesWithAPerSecondSuffix()
    {
        var result = PerformanceReport.Build(Live(rx: 1_073_741_824, tx: 512), "x");

        result.Summary.Should().Contain("Receiving 1 GB/s").And.Contain("sending 512 B/s");
    }

    /// <summary>
    /// Locks the SSE <c>tool.result.result</c> wire shape the SPA depends on byte-for-byte, serialized
    /// through the SAME options the Service's SseTurnWriter uses (Web camelCase + camelCase enums). The
    /// card carries the tool name, confidence, subject, and the metrics data with exact camelCase keys;
    /// an unmeasured axis is JSON <c>null</c>, never 0.
    /// </summary>
    [Fact]
    public void ToolResultCard_SerializesTheFrozenGetPerformanceWireShape()
    {
        // Mirrors SseTurnWriter.Json: JsonSerializerDefaults.Web + camelCase enum strings.
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };

        var report = PerformanceReport.Build(
            new ServerMetricsReading(PerformanceState.Live,
                CpuPctCore: 42.5, MemBytes: 1024, RxBps: null, TxBps: null,
                IoReadBps: 4096, IoWriteBps: 512, DiskBytes: 5000, Pids: 7),
            "terraria-test");
        var card = ToolResultCard.From(report);

        var node = JsonNode.Parse(JsonSerializer.Serialize(card, json))!.AsObject();

        node["tool"]!.GetValue<string>().Should().Be("get_performance");
        node["confidence"]!.GetValue<string>().Should().Be("confirmed");
        node["subject"]!["resource"]!.GetValue<string>().Should().Be("metrics");
        node["subject"]!["id"]!.GetValue<string>().Should().Be("terraria-test");

        var data = node["data"]!.AsObject();
        data["instance"]!.GetValue<string>().Should().Be("terraria-test");
        data["state"]!.GetValue<string>().Should().Be("live");
        data["cpuPctCore"]!.GetValue<double>().Should().Be(42.5);
        data["memBytes"]!.GetValue<long>().Should().Be(1024);
        data["ioReadBps"]!.GetValue<long>().Should().Be(4096);
        data["ioWriteBps"]!.GetValue<long>().Should().Be(512);
        data["diskBytes"]!.GetValue<long>().Should().Be(5000);
        data["pids"]!.GetValue<int>().Should().Be(7);
        // Unmeasured network → JSON null (the key is present, its value is null), never a fabricated 0.
        data.ContainsKey("rxBps").Should().BeTrue();
        data["rxBps"].Should().BeNull();
        data.ContainsKey("txBps").Should().BeTrue();
        data["txBps"].Should().BeNull();
    }

    // --- BuildHistory (the windowed TREND read → chart card) ----------------------------------------

    private static ServerMetricsHistory History(params (string metric, double[] values)[] series)
    {
        var dict = new Dictionary<string, IReadOnlyList<MetricPoint>>();
        var t0 = new DateTimeOffset(2026, 7, 18, 0, 0, 0, TimeSpan.Zero);
        foreach (var (metric, values) in series)
            dict[metric] = values.Select((v, i) => new MetricPoint(t0.AddMinutes(i), v)).ToList();
        return new ServerMetricsHistory(PerformanceState.Live, "1h", "raw", dict);
    }

    [Fact]
    public void History_Live_ReportsCpuAndMemoryAvgPeak_AndCarriesTheSeries()
    {
        var result = PerformanceReport.BuildHistory(
            History(("cpuPctCore", [10, 30, 20]), ("memBytes", [1_073_741_824, 1_073_741_824, 1_073_741_824])),
            "minecraft");

        result.Tool.Should().Be(ResultCardKinds.Performance);
        result.Confidence.Should().Be(Confidence.Confirmed);
        result.Summary.Should().Contain("last 1h").And.Contain("averaged 20%").And.Contain("peak 30%");
        result.Data.Range.Should().Be("1h");
        result.Data.Series.Should().ContainKey("cpuPctCore");
        result.Data.Series!["cpuPctCore"].Should().HaveCount(3);
        // A trend read carries no snapshot scalars — the chart is the payload.
        result.Data.CpuPctCore.Should().BeNull();
    }

    [Fact]
    public void History_EmptySeries_IsHonestNoData_NotIdle()
    {
        var result = PerformanceReport.BuildHistory(
            new ServerMetricsHistory(PerformanceState.Live, "24h", "raw", new Dictionary<string, IReadOnlyList<MetricPoint>>()),
            "terraria");

        result.Summary.Should().Contain("No resource history").And.Contain("last 24h")
            .And.Contain("isn't a sign it's idle");
        result.Data.Series.Should().BeEmpty();
    }

    [Fact]
    public void History_MonitorUnavailable_IsHonestCouldntRead_NotIdle()
    {
        var result = PerformanceReport.BuildHistory(
            new ServerMetricsHistory(PerformanceState.MonitorUnavailable, "7d", null, new Dictionary<string, IReadOnlyList<MetricPoint>>()),
            "minecraft");

        result.Summary.Should().Contain("unavailable").And.Contain("isn't a sign the server is idle");
        result.Confidence.Should().Be(Confidence.Possible);
    }

    [Fact]
    public void History_SerializesSeriesInTheCard_ForTheChart()
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };

        var result = PerformanceReport.BuildHistory(History(("cpuPctCore", [12.5, 40.0])), "terraria-test");
        var card = ToolResultCard.From(result);
        var node = JsonNode.Parse(JsonSerializer.Serialize(card, json))!.AsObject();

        node["tool"]!.GetValue<string>().Should().Be("get_performance");
        var data = node["data"]!.AsObject();
        data["range"]!.GetValue<string>().Should().Be("1h");
        var points = data["series"]!["cpuPctCore"]!.AsArray();
        points.Count.Should().Be(2);
        points[0]!["value"]!.GetValue<double>().Should().Be(12.5);
        points[0]!["ts"].Should().NotBeNull();
    }
}
