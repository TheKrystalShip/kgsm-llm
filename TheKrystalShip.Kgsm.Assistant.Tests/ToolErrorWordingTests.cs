using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Blueprints;
using TheKrystalShip.Kgsm.Assistant.Fetch;
using TheKrystalShip.Kgsm.Assistant.Metrics;
using TheKrystalShip.Kgsm.Assistant.Network;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Kgsm.Assistant.Search;
using TheKrystalShip.Kgsm.Assistant.Status;
using TheKrystalShip.Llm.Conversation;
using TheKrystalShip.Llm.Models;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// What a refused call actually SAYS.
/// <para>
/// A refusal is read by a model whose only move is another call, so it has to name an action that
/// model can take. One that states only what was missing leaves nothing to change between one call
/// and the next, and the measured behaviour then is the identical call, repeated until the turn's
/// budget is gone — so these assert that every refusal carrying a knowable answer states it.
/// </para>
/// </summary>
public class ToolErrorWordingTests
{
    private readonly IServerOperations _operations = Substitute.For<IServerOperations>();
    private readonly IServerInventory _inventory = Substitute.For<IServerInventory>();
    private readonly IConfirmationContext _confirmations = Substitute.For<IConfirmationContext>();
    private readonly ISearch _search = Substitute.For<ISearch>();
    private readonly IWebFetch _webFetch = Substitute.For<IWebFetch>();
    private readonly IServerMetrics _metrics = Substitute.For<IServerMetrics>();
    private readonly IEventHistory _events = Substitute.For<IEventHistory>();
    private readonly INetworkInfo _network = Substitute.For<INetworkInfo>();
    private readonly IUpnpInfo _upnp = Substitute.For<IUpnpInfo>();
    private readonly IHostFacts _hostFacts = Substitute.For<IHostFacts>();
    private readonly IServerFacts _facts = Substitute.For<IServerFacts>();
    private readonly IBlueprintAuthoring _blueprintAuthoring = Substitute.For<IBlueprintAuthoring>();

    public ToolErrorWordingTests()
    {
        _inventory.GetInstancesAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>
            {
                ["Ketchup"] = "palworld",
                ["minecraft"] = "minecraft",
                ["romestead"] = "rome",
            });
        _inventory.GetBlueprintCatalogAsync(Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new BlueprintSummary("factorio", "Factorio"),
                new BlueprintSummary("terraria", "Terraria"),
            });
    }

    private ToolDispatcher Create() =>
        new(_operations, _inventory, _confirmations, _search, _webFetch, _metrics, _events,
            _network, _upnp, _facts, _hostFacts, _blueprintAuthoring, ShippedText.Catalog,
            new SettlementTiming(TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(10)),
            new InMemoryMemoryStore(), Options.Create(new MemoryOptions()),
            NullLogger<ToolDispatcher>.Instance);

    private async Task<string> Run(Capability capability, Dictionary<string, string?> args) =>
        (await Create().ExecuteAsync(new LlmToolCall(ShippedText.Name(capability), args))).Summary;

    /// <summary>
    /// The per-instance reads, called with no subject. This is the shape that cost a real turn: asked
    /// "any of the servers need a backup?", the model called the backups read fleet-wide, was told
    /// only that it reports on one server, and re-sent the identical call before working out the loop
    /// on its own.
    /// </summary>
    public static TheoryData<Capability> PerInstanceReads() =>
        new(LlmTools.GetInstanceConfig, LlmTools.GetInstanceVersion, LlmTools.GetInstanceNote);

    [Theory]
    [MemberData(nameof(PerInstanceReads))]
    public async Task APerInstanceRead_WithNoSubject_NamesTheServersAndTheLoop(Capability capability)
    {
        var summary = await Run(capability, new Dictionary<string, string?>());

        summary.Should().StartWith("Error:");
        summary.Should().Contain("once per server",
            "a fleet-wide question asked of a per-instance tool is answered by calling it repeatedly, "
            + "and the refusal has to say so");
        summary.Should().Contain("Ketchup").And.Contain("minecraft").And.Contain("romestead");
    }

    [Fact]
    public async Task ABlankInstanceName_ListsTheInstances_SameAsATypoDoes()
    {
        var missing = await Run(LlmTools.ReadFile, new Dictionary<string, string?>
        {
            ["path"] = "server.properties",
        });
        var typo = await Run(LlmTools.ReadFile, new Dictionary<string, string?>
        {
            ["instance_name"] = "ketchupp-2",
            ["path"] = "server.properties",
        });

        // The blank case is the one that most needs the roster and was the one withholding it.
        missing.Should().Contain("Ketchup").And.Contain("minecraft").And.Contain("romestead");
        typo.Should().Contain("Ketchup").And.Contain("minecraft").And.Contain("romestead");
    }

    [Fact]
    public async Task ABlankBlueprintName_ListsTheInstallableGames()
    {
        var summary = await Run(LlmTools.InstallServer, new Dictionary<string, string?>());

        summary.Should().StartWith("Error:");
        summary.Should().Contain("Factorio").And.Contain("Terraria");
    }

    [Fact]
    public async Task ABlankConfigKey_NamesTheToolThatListsTheKeys()
    {
        var summary = await Run(LlmTools.SetConfigValue, new Dictionary<string, string?>
        {
            ["instance_name"] = "Ketchup",
        });

        summary.Should().StartWith("Error:");
        summary.Should().Contain(ShippedText.Name(LlmTools.GetInstanceConfig).ToString());
    }

    [Fact]
    public async Task ABlankPath_NamesTheToolsThatFindOne()
    {
        var summary = await Run(LlmTools.WriteFile, new Dictionary<string, string?>
        {
            ["instance_name"] = "Ketchup",
        });

        summary.Should().StartWith("Error:");
        summary.Should().Contain(ShippedText.Name(LlmTools.FindFiles).ToString());
        summary.Should().Contain(ShippedText.Name(LlmTools.SearchFiles).ToString());
    }

    [Fact]
    public async Task AnInventedToolName_SaysTheOfferedListIsAll_ThereIs()
    {
        var summary = (await Create().ExecuteAsync(
            new LlmToolCall(new Tool("restart_everything"), new Dictionary<string, string?>()))).Summary;

        summary.Should().StartWith("Error:");
        summary.Should().Contain("exact name");
    }

    /// <summary>
    /// A misspelled argument NAME, on the tool where getting it wrong is silent. Asked about one
    /// server, the model sent <c>instance_nameless</c>; the status read found no
    /// <c>instance_name</c>, answered for the whole fleet, and said nothing about the server that was
    /// never looked at.
    /// </summary>
    [Fact]
    public async Task AMisspelledArgumentName_IsRefused_WithTheNamesTheToolTakes()
    {
        var summary = await Run(LlmTools.ServerInfo, new Dictionary<string, string?>
        {
            ["instance_nameless"] = "Ketchup",
        });

        summary.Should().StartWith("Error:");
        summary.Should().Contain("'instance_nameless'");
        summary.Should().Contain("'instance_name'");
    }

    /// <summary>
    /// The one argument name the catalog does not declare and the dispatcher reads anyway. The
    /// tolerance is worth more than the diagnostic, so the check has to leave it standing.
    /// </summary>
    [Fact]
    public async Task ADeliberateAlias_IsNotTreatedAsAMisspelling()
    {
        _operations.SearchInstanceFilesAsync(
                "Ketchup", "MaxPlayers", null, true, Arg.Any<CancellationToken>())
            .Returns(Result<InstanceContentMatches>.Success(new InstanceContentMatches(
                new[] { new InstanceContentMatch("server.properties", 12, "MaxPlayers=20") },
                Truncated: false, Incomplete: false)));

        var summary = await Run(LlmTools.SearchFiles, new Dictionary<string, string?>
        {
            ["instance_name"] = "Ketchup",
            ["pattern"] = "MaxPlayers",
        });

        summary.Should().NotStartWith("Error:");
        summary.Should().Contain("server.properties");
    }

    /// <summary>
    /// A read that could not run is not a reading of zero. The refusal says which of the two it is,
    /// because the model's next move is to report something to a person and the two are opposite.
    /// </summary>
    [Fact]
    public async Task AFailedFileRead_SaysItFailed_NotThatTheFileIsEmpty()
    {
        _operations.ReadInstanceFileAsync("Ketchup", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Failure("permission denied"));

        var summary = await Run(LlmTools.ReadFile, new Dictionary<string, string?>
        {
            ["instance_name"] = "Ketchup",
            ["path"] = "server.properties",
        });

        summary.Should().StartWith("Error:");
        summary.Should().Contain("permission denied");
        summary.Should().Contain("not an empty or missing file");
    }
}
