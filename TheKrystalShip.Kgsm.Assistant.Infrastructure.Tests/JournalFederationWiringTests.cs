using FluentAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.Kgsm.Assistant.Extensions;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Extensions;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Services;

using Xunit;

/// <summary>
/// The assistant reads every producer's journal, not the engine's alone.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>trace_root_cause</c> is what this protects.</b> It correlates an event timeline against a
/// metrics window and a health snapshot to explain why a server went down — and the crash, the
/// give-up and who was playing are the <em>supervisor's</em> events, the port edges are the firewall
/// authority's, and a threshold episode is the monitor's. Reading the engine's journal alone, the
/// tool searches for the cause of a crash in a record that does not contain the crash, finds
/// nothing, and says so honestly. Nothing fails; the answer is simply always "no correlation".
/// </para>
/// <para>
/// <b>The call order is the thing under test.</b> <c>AddKgsmAdapters</c> registers the federated
/// pair and <c>AddKgsmEventListener</c> runs after it, adding dispatch on top of that source. A
/// listener that registered a reader of its own would be last, and last wins — so the composed graph
/// is what has to be asserted, not either call alone, which would pass trivially.
/// </para>
/// </remarks>
public class JournalFederationWiringTests
{
    private static ServiceProvider BuildComposedProvider()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KGSM:Path"] = "/opt/kgsm/kgsm.sh",
                // Present, so the listener is switched on for this host and its registrations run.
                ["KGSM:JournalDir"] = "/var/lib/kgsm/events",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddKgsmAssistant();
        services.AddKgsmAdapters(config);
        services.AddKgsmEventListener(config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void ReadingHistoryBack_CoversEveryProducersJournal()
    {
        using var provider = BuildComposedProvider();

        provider.GetRequiredService<IEventJournalHistory>()
            .Should().BeOfType<FederatedEventJournalHistory>(
                "the incident tools read history, and the crash they are asked to explain is in the "
                + "supervisor's journal rather than the engine's");
    }

    [Fact]
    public void TheListener_TailsEveryProducersJournal_NotAReaderOfItsOwn()
    {
        using var provider = BuildComposedProvider();

        provider.GetRequiredService<IEventSource>().Should().BeOfType<FederatedEventSource>(
            "AddKgsmEventListener runs last, so a source registered inside it would win over the "
            + "federated one AddKgsmAdapters registered");
    }
}
