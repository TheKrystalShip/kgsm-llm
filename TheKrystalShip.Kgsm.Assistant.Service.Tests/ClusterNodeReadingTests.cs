using System.Net;
using System.Text;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Infrastructure;
using TheKrystalShip.Kgsm.Assistant.Audit;
using TheKrystalShip.Kgsm.Assistant.Metrics;
using TheKrystalShip.Kgsm.Assistant.Network;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Kgsm.Assistant.Service.Cluster;
using TheKrystalShip.KGSM.Cluster.Identity;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

/// <summary>
/// Reading a node, against a node that answers exactly what a real one would.
/// </summary>
/// <remarks>
/// <para>
/// Driven through the real <see cref="NodeApiClient"/> and the real contract package rather than
/// around them, so what is under test includes the deserialization. A mapping asserted against a
/// hand-built DTO would pass while the wire names were wrong, which is the failure this whole package
/// exists to make impossible.
/// </para>
/// <para>
/// Every case here is one where an absence and a measurement arrive looking alike — no conflicts and
/// no scan, no sample and no monitor — and the adapter has to keep them apart.
/// </para>
/// </remarks>
public sealed class ClusterNodeReadingTests
{
    /// <summary>
    /// A port whose open state the node could not establish is not a rule the firewall holds open, and
    /// its presence means the list cannot claim to be the whole set.
    /// </summary>
    [Fact]
    public async Task An_unestablished_port_is_neither_open_nor_counted_as_enumerated()
    {
        using var node = new StubNode();
        node.Answer("/api/v1/servers", """[{"id":"mc","name":"mc","blueprint":"minecraft","status":"running"}]""");
        node.Answer("/api/v1/servers/mc", """
            {"id":"mc","name":"mc","blueprint":"minecraft","status":"running",
             "network":{"firewall":"ufw","required":[
               {"port":25565,"proto":"tcp","open":true},
               {"port":25565,"proto":"udp","open":null}]}}
            """);

        NetworkReading reading = await node.Network().GetPortsAsync("mc");

        reading.State.Should().Be(NetworkState.Available);
        reading.Backend.Should().Be("ufw");
        reading.Ports.Should().ContainSingle().Which.Protocol.Should().Be("tcp");
        reading.ListState.Should().Be(PortListState.Unknown);
        // Whether the firewall is enforcing is that machine's own daemon's business and is not on the
        // wire, so it stays unknown rather than being assumed from ports having answered.
        reading.Enforcement.Should().Be(NetworkEnforcement.Unknown);
    }

    /// <summary>
    /// A stopped server has no live sample and that is its ordinary state; a node that could not be
    /// asked is an outage. Told apart, because a caller sent to look at a broken monitor when the
    /// server is merely stopped is sent to the wrong place.
    /// </summary>
    [Fact]
    public async Task A_stopped_server_is_not_an_unavailable_monitor()
    {
        using var node = new StubNode();
        node.Answer("/api/v1/servers", """[{"id":"mc","name":"mc","blueprint":"minecraft","status":"stopped"}]""");
        node.Answer("/api/v1/servers/mc", """
            {"id":"mc","name":"mc","blueprint":"minecraft","status":"stopped","diskBytes":2547768738}
            """);

        ServerMetricsReading reading = await node.Metrics().GetSnapshotAsync("mc");

        reading.State.Should().Be(PerformanceState.NotRunning);
        reading.CpuPctCore.Should().BeNull();
        // The space it occupies is a property of its files, so it survives the server being stopped.
        reading.DiskBytes.Should().Be(2547768738);
    }

    [Fact]
    public async Task A_running_server_carries_the_nodes_own_sample()
    {
        using var node = new StubNode();
        node.Answer("/api/v1/servers", """[{"id":"mc","name":"mc","blueprint":"minecraft","status":"running"}]""");
        node.Answer("/api/v1/servers/mc", """
            {"id":"mc","name":"mc","blueprint":"minecraft","status":"running","diskBytes":100,
             "metrics":{"cpuPctCore":37.5,"memBytes":2048,"pids":9,"rxBps":10,"txBps":20}}
            """);

        ServerMetricsReading reading = await node.Metrics().GetSnapshotAsync("mc");

        reading.State.Should().Be(PerformanceState.Live);
        reading.CpuPctCore.Should().Be(37.5);
        reading.MemBytes.Should().Be(2048);
        reading.Pids.Should().Be(9);
    }

    /// <summary>
    /// A node that will not answer leaves the reading unavailable rather than empty. An empty roster of
    /// backups is a real fact about a server; a node that could not be asked is not.
    /// </summary>
    [Fact]
    public async Task A_node_that_refuses_leaves_the_reading_unavailable()
    {
        using var node = new StubNode();
        node.Answer("/api/v1/servers", """[{"id":"mc","name":"mc","blueprint":"minecraft","status":"running"}]""");
        node.Refuse("/api/v1/servers/mc/backups", HttpStatusCode.ServiceUnavailable);

        BackupListing listing = await node.Facts().GetBackupsAsync("mc");

        listing.State.Should().Be(FactsState.Unavailable);
        listing.Backups.Should().BeEmpty();
    }

    /// <summary>
    /// A backup's id is what a restore names, so it survives the wire byte for byte — not shortened,
    /// not prettified. The whole point of reading it is to be able to pass it back.
    /// </summary>
    [Fact]
    public async Task A_backups_id_survives_intact()
    {
        using var node = new StubNode();
        node.Answer("/api/v1/servers", """[{"id":"mc","name":"mc","blueprint":"minecraft","status":"running"}]""");
        node.Answer("/api/v1/servers/mc/backups", """
            {"serverId":"mc","backups":[{"name":"minecraft-20260831T050058Z-0c23b4",
             "createdAt":"2026-08-31T05:02:05Z","sizeBytes":2547768738,"sources":["install"]}]}
            """);

        BackupListing listing = await node.Facts().GetBackupsAsync("mc");

        listing.State.Should().Be(FactsState.Available);
        BackupEntry entry = listing.Backups.Should().ContainSingle().Subject;
        entry.Id.Should().Be("minecraft-20260831T050058Z-0c23b4");
        entry.SizeBytes.Should().Be(2547768738);
        entry.Contents.Should().Equal("install");
    }

    /// <summary>
    /// A roster read carries the mechanism the supervisor named, because an empty roster under RCON and
    /// an empty roster under log-matching are worth different things — the first cannot see somebody
    /// who joined and left between two polls.
    /// </summary>
    [Theory]
    [InlineData("log", PresenceDetection.Log)]
    [InlineData("rcon", PresenceDetection.Rcon)]
    [InlineData("none", PresenceDetection.None)]
    [InlineData("something-this-build-does-not-know", PresenceDetection.Unknown)]
    public async Task The_presence_mechanism_is_carried_not_guessed(string reported, PresenceDetection expected)
    {
        using var node = new StubNode();
        node.Answer("/api/v1/servers", """[{"id":"mc","name":"mc","blueprint":"minecraft","status":"running"}]""");
        node.Answer("/api/v1/servers/mc/players", $$$"""
            {"detection":"configured","mechanism":"{{{reported}}}","players":[],
             "moderation":{"kick":true,"ban":true,"unban":true,"targetKind":"name"}}
            """);

        PresenceReading reading = await node.Facts().GetPresenceAsync();

        reading.Instances.Should().ContainSingle().Which.Detection.Should().Be(expected);
    }

    /// <summary>
    /// A history short by one machine's events looks exactly like a quieter cluster, and no row in the
    /// list says which — so a read that lost a node reports the journal as unavailable rather than
    /// serving what it did get as if it were everything.
    /// </summary>
    [Fact]
    public async Task A_lost_node_makes_the_whole_history_unavailable()
    {
        using var node = new StubNode();
        node.Answer("/api/v1/servers", """[{"id":"mc","name":"mc","blueprint":"minecraft","status":"running"}]""");
        node.Refuse("/api/v1/audit", HttpStatusCode.InternalServerError);

        var history = new ClusterEventHistory(node.Servers(), node.Directory, node.Client);

        EventHistoryReading reading = await history.GetEventsAsync(null, null, 50);

        reading.State.Should().Be(AuditReadState.JournalUnavailable);
        reading.Events.Should().BeEmpty();
    }

    /// <summary>
    /// One node in the roster, so the reading is about a machine and can be given. The multi-node
    /// refusal is the interesting half and is asserted beside it.
    /// </summary>
    [Fact]
    public async Task Host_vitals_answer_for_the_one_node_there_is()
    {
        using var node = new StubNode();
        node.Answer($"/api/v1/hosts/{StubNode.Member}", """
            {"id":"n1","mem":{"used":14.0,"total":31.0,"available":17.0},
             "disks":[{"mount":"/","used":330.0,"total":916.0}],
             "load":{"one":0.5,"five":0.7,"fifteen":0.9}}
            """);

        HostFacts facts = await new ClusterHostFacts(node.Directory, node.Client).GetAsync();

        facts.State.Should().Be(FactsState.Available);
        facts.Memory!.Total.Should().Be("31G");
        facts.Memory.Available.Should().Be("17G");
        facts.Disk!.UsedPercent.Should().Be(36);
        facts.Load!.OneMin.Should().Be("0.50");
    }

    /// <summary>
    /// With more than one machine there is no "the host", and the refusal names them — a caller told
    /// only that the read failed would go looking for a broken machine instead of asking a narrower
    /// question.
    /// </summary>
    [Fact]
    public async Task Host_vitals_refuse_a_fleet_and_say_which_machines()
    {
        using var node = new StubNode(members: ["n1", "n2"]);

        HostFacts facts = await new ClusterHostFacts(node.Directory, node.Client).GetAsync();

        facts.State.Should().Be(FactsState.Unavailable);
        facts.Reason.Should().Contain("n1").And.Contain("n2");
        facts.Reason.Should().Contain("one machine");
    }
}
