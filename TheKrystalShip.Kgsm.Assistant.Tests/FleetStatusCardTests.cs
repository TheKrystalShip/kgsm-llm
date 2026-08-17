using FluentAssertions;

using TheKrystalShip.Kgsm.Assistant.Envelope;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Kgsm.Assistant.Status;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// The pure <see cref="FleetStatusCard"/> projection (get_status fleet-mode card, Phase 2 §5·b).
/// The load-bearing rule mirrors the health card's stopped≠failure doctrine: an instance whose
/// status could not be READ must map to <see cref="ServerRunState.Unknown"/> (with its reason),
/// never collapse into <see cref="ServerRunState.Stopped"/> — a bare "not running" would masquerade
/// as a measured fact. The summary string is also locked byte-identical to the dispatcher's prior
/// inline output (model grounding must not drift when the card lights up).
/// </summary>
public class FleetStatusCardTests
{
    private static FleetStatusEntry Read(string instance, bool running) =>
        new(instance, FleetStatusAvailability.Read, running, null);

    private static FleetStatusEntry Unavailable(string instance, string reason) =>
        new(instance, FleetStatusAvailability.Unavailable, null, reason);

    [Fact]
    public void Build_MixedFleet_NeverCollapsesUnavailableIntoStopped_AndCountsHonestly()
    {
        var card = FleetStatusCard.Build(new[]
        {
            Read("minecraft", running: true),
            Read("terraria", running: false),
            Unavailable("broken", "its management file must be regenerated to report status"),
        });

        // The card kind + host-scoped subject (architecture.html "primary" convention; §7 sign-off).
        card.Tool.Should().Be(ResultCardKinds.Status);
        card.Confidence.Should().Be(Confidence.Confirmed);
        card.Subject.Should().Be(new ResultRef(ResourceKind.Host, "primary"));

        var byName = card.Data.Servers.ToDictionary(s => s.Instance);

        byName["minecraft"].State.Should().Be(ServerRunState.Running);
        byName["minecraft"].Severity.Should().Be(Severity.Success);

        byName["terraria"].State.Should().Be(ServerRunState.Stopped);
        byName["terraria"].Severity.Should().Be(Severity.Info);

        // THE invariant: a could-not-read instance is Unknown/Warn with its reason — never Stopped.
        byName["broken"].State.Should().Be(ServerRunState.Unknown);
        byName["broken"].State.Should().NotBe(ServerRunState.Stopped);
        byName["broken"].Severity.Should().Be(Severity.Warn);
        byName["broken"].Reason.Should().Be("its management file must be regenerated to report status");

        // Honest counts: the unreadable one lands in Unavailable, never inflating Stopped.
        card.Data.Total.Should().Be(3);
        card.Data.Running.Should().Be(1);
        card.Data.Stopped.Should().Be(1);
        card.Data.Unavailable.Should().Be(1);
    }

    [Fact]
    public void Build_Summary_StaysByteIdenticalToLegacyFormat()
    {
        var card = FleetStatusCard.Build(new[]
        {
            Read("minecraft", running: true),
            Read("terraria", running: false),
            Unavailable("broken", "regenerate it"),
        });

        // Exactly the dispatcher's prior inline string — the model's grounding text must not change.
        card.Summary.Should().Be(
            "Status of all 3 server(s):\n" +
            "- minecraft: running\n" +
            "- terraria: stopped\n" +
            "- broken: status unavailable (regenerate it)");
    }

    [Fact]
    public void Build_EmptyFleet_YieldsAnEmptyCard_NotDropped()
    {
        // A successful read of zero servers is a real measured result → an empty card (Total 0),
        // not a summary-only drop. The summary keeps the legacy wording.
        var card = FleetStatusCard.Build(Array.Empty<FleetStatusEntry>());

        card.Data.Servers.Should().BeEmpty();
        card.Data.Total.Should().Be(0);
        card.Data.Running.Should().Be(0);
        card.Data.Stopped.Should().Be(0);
        card.Data.Unavailable.Should().Be(0);
        card.Summary.Should().Be("There are no installed server instances.");
    }
}
