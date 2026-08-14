using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Infrastructure.Configuration;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Kgsm;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Tests;

public class KgsmServerInventoryTests
{
    private readonly IInstanceService _instances = Substitute.For<IInstanceService>();
    private readonly IBlueprintService _blueprints = Substitute.For<IBlueprintService>();

    private KgsmServerInventory Create(int ttl = 300) =>
        new(_instances, _blueprints,
            Options.Create(new InventoryCacheOptions { InstancesTtlSeconds = ttl, BlueprintsTtlSeconds = ttl }),
            NullLogger<KgsmServerInventory>.Instance);

    private static IReadOnlyDictionary<string, Instance> Inst(params (string name, string bp)[] items) =>
        items.ToDictionary(i => i.name, i => new Instance { Name = i.name, BlueprintFile = i.bp });

    [Fact]
    public async Task GetInstances_MapsNameToBlueprint()
    {
        _instances.GetAll().Returns(Inst(("terraria", "terraria"), ("mc", "minecraft")));

        var result = await Create().GetInstancesAsync();

        result["terraria"].Should().Be("terraria");
        result["mc"].Should().Be("minecraft");
    }

    [Fact]
    public async Task WithinTtl_CachesAndCallsKgsmOnce()
    {
        _instances.GetAll().Returns(Inst(("terraria", "terraria")));
        var inv = Create(ttl: 300);

        await inv.GetInstancesAsync();
        await inv.GetInstancesAsync();

        _instances.Received(1).GetAll();
    }

    [Fact]
    public async Task Invalidate_ForcesRefresh()
    {
        _instances.GetAll().Returns(Inst(("terraria", "terraria")));
        var inv = Create(ttl: 300);

        await inv.GetInstancesAsync();
        inv.Invalidate();
        await inv.GetInstancesAsync();

        _instances.Received(2).GetAll();
    }

    [Fact]
    public async Task InvalidateInstances_RefreshesTheRosterAndLeavesTheCatalog()
    {
        _instances.GetAll().Returns(Inst(("terraria", "terraria")));
        _blueprints.ListDetailed().Returns(new Dictionary<string, Blueprint> { ["terraria"] = new() { Name = "terraria" } });
        var inv = Create(ttl: 300);

        await inv.GetInstancesAsync();
        await inv.GetBlueprintNamesAsync();
        inv.InvalidateInstances();
        await inv.GetInstancesAsync();
        await inv.GetBlueprintNamesAsync();

        _instances.Received(2).GetAll();
        _blueprints.Received(1).ListDetailed();
    }

    [Fact]
    public async Task InvalidateBlueprints_RefreshesTheCatalogAndLeavesTheRoster()
    {
        _instances.GetAll().Returns(Inst(("terraria", "terraria")));
        _blueprints.ListDetailed().Returns(new Dictionary<string, Blueprint> { ["terraria"] = new() { Name = "terraria" } });
        var inv = Create(ttl: 300);

        await inv.GetInstancesAsync();
        await inv.GetBlueprintNamesAsync();
        inv.InvalidateBlueprints();
        await inv.GetInstancesAsync();
        await inv.GetBlueprintNamesAsync();

        _instances.Received(1).GetAll();
        _blueprints.Received(2).ListDetailed();
    }

    /// <summary>
    /// The TTL is the backstop behind the engine's events, not something they replace: an event
    /// missed while this process was restarting must not be believed forever.
    /// </summary>
    [Fact]
    public async Task WithNoEventAtAll_TheTtlStillRefreshes()
    {
        _instances.GetAll().Returns(Inst(("terraria", "terraria")));
        var inv = Create(ttl: 0);

        await inv.GetInstancesAsync();
        await inv.GetInstancesAsync();

        _instances.Received(2).GetAll();
    }

    [Fact]
    public async Task RefreshFailure_ServesLastKnownGood()
    {
        _instances.GetAll().Returns(
            _ => Inst(("terraria", "terraria")),
            _ => throw new InvalidOperationException("boom"));
        var inv = Create(ttl: 300);

        var first = await inv.GetInstancesAsync();
        inv.Invalidate();
        var second = await inv.GetInstancesAsync(); // refresh throws -> keep the snapshot

        second.Should().BeEquivalentTo(first);
        _instances.Received(2).GetAll();
    }

    [Fact]
    public async Task GetBlueprintNames_ReturnsKeys()
    {
        _blueprints.ListDetailed().Returns(new Dictionary<string, Blueprint>
        {
            ["valheim"] = new Blueprint { Name = "valheim" },
            ["terraria"] = new Blueprint { Name = "terraria" },
        });

        var names = await Create().GetBlueprintNamesAsync();

        names.Should().BeEquivalentTo("valheim", "terraria");
    }

    [Fact]
    public async Task GetBlueprintCatalog_CarriesTheDisplayNameTheBlueprintDeclares()
    {
        _blueprints.ListDetailed().Returns(new Dictionary<string, Blueprint>
        {
            ["projectzomboid"] = new Blueprint
            {
                Name = "projectzomboid",
                Metadata = new BlueprintMetadata { DisplayName = "Project Zomboid" },
            },
            ["homebrew"] = new Blueprint { Name = "homebrew" },
        });

        var catalog = await Create().GetBlueprintCatalogAsync();

        catalog.Should().ContainSingle(b => b.Name == "projectzomboid")
            .Which.DisplayName.Should().Be("Project Zomboid");
        // A blueprint declaring none says so — the label falls back, the field does not fabricate.
        var homebrew = catalog.Single(b => b.Name == "homebrew");
        homebrew.DisplayName.Should().BeNull();
        homebrew.Label.Should().Be("homebrew");
    }

    /// <summary>
    /// Both blueprint views are one read. The engine call behind them takes seconds, so a surface asking
    /// for names and a surface asking for the catalog must not cost two of them.
    /// </summary>
    [Fact]
    public async Task NamesAndCatalog_ShareOneEngineRead()
    {
        _blueprints.ListDetailed().Returns(new Dictionary<string, Blueprint>
        {
            ["terraria"] = new Blueprint { Name = "terraria" },
        });
        var inv = Create(ttl: 300);

        await inv.GetBlueprintNamesAsync();
        await inv.GetBlueprintCatalogAsync();

        _blueprints.Received(1).ListDetailed();
    }
}
