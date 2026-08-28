using FluentAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.Kgsm.Assistant.Extensions;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Extensions;
using TheKrystalShip.Kgsm.Assistant.Ports;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Tests;

/// <summary>
/// A host that is not a leaf writes no journal.
/// </summary>
/// <remarks>
/// <para>
/// <b>The failure this prevents has happened elsewhere in the ecosystem.</b> kgsm-api's test suite
/// used a deliberately fake-free host, inherited the real machine's paths, and wrote 25 ready/stop
/// pairs into the <em>live</em> journal — harmless right up until something started writing at startup.
/// </para>
/// <para>
/// The same shape is live here and larger. <c>AddKgsmAdapters</c> is composed by the resident service,
/// the CLI and the benchmark alike: a writer registered in it would put a lifecycle pair in the live
/// journal on every <c>kgsm-assistant-cli</c> invocation, and hundreds of turn-quality lines in it on
/// every eval run — a record of things no person did, in the file the incident tools read back.
/// </para>
/// <para>
/// So the default is a journal that writes nowhere, and only the resident service registers a real one
/// after it. This asserts the composed graph, because that is the thing that can be wrong.
/// </para>
/// </remarks>
public class AssistantJournalWiringTests
{
    private static ServiceProvider BuildComposedProvider()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KGSM:Path"] = "/opt/kgsm/kgsm.sh",
                ["KGSM:JournalDir"] = "/var/lib/kgsm/events",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        // The same three calls the CLI's Program.cs makes, in the same order.
        services.AddKgsmAssistant();
        services.AddKgsmAdapters(config);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The shared graph resolves the journal that writes nowhere.
    /// </summary>
    /// <remarks>
    /// The composed graph, not <c>AddKgsmAssistant</c> alone. <c>AddKgsmAdapters</c> runs after it and
    /// is where the concrete adapters win over the library's fail-closed defaults — a real journal
    /// registered there would win in exactly the same way, and asserting either call on its own would
    /// pass while the composition was wrong.
    /// </remarks>
    [Fact]
    public void TheSharedGraph_WritesNoJournal()
    {
        using ServiceProvider provider = BuildComposedProvider();

        provider.GetRequiredService<IAssistantJournal>()
            .Should().BeOfType<NoAssistantJournal>(
                "the CLI and the benchmark compose this graph and neither is a leaf — a real writer "
                + "here would record a one-shot's startup and an eval's every turn in the live journal");
    }

    /// <summary>
    /// Nothing in the shared graph can reach a journal writer at all.
    /// </summary>
    /// <remarks>
    /// The stronger half. The check above says which implementation won; this says there is nothing to
    /// win with — no writer is in the container for something registered later to pick up by accident.
    /// </remarks>
    [Fact]
    public void TheSharedGraph_HasNoJournalWriterToReach()
    {
        using ServiceProvider provider = BuildComposedProvider();

        provider.GetService<KGSM.Core.Interfaces.IEventJournalWriter>()
            .Should().BeNull(
                "only the resident service registers the write half; the shared graph reads every "
                + "producer's journal and writes to none");
    }
}
