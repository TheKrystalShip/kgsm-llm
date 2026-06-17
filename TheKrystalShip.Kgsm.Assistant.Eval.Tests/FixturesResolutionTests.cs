using FluentAssertions;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Eval.Tests;

public class FixturesResolutionTests
{
    private static IServerInventory Inventory(IReadOnlyDictionary<string, string> instances, params string[] blueprints)
    {
        var inv = Substitute.For<IServerInventory>();
        inv.GetInstancesAsync(Arg.Any<CancellationToken>()).Returns(instances);
        inv.GetBlueprintNamesAsync(Arg.Any<CancellationToken>()).Returns(blueprints);
        return inv;
    }

    [Fact]
    public async Task Resolves_roles_from_inventory_and_runstate()
    {
        var inv = Inventory(
            new Dictionary<string, string> { ["factorio-test"] = "factorio.bp", ["mc-1"] = "minecraft.bp" },
            "factorio", "minecraft", "valheim");
        var ops = Substitute.For<IServerOperations>();
        ops.IsActiveAsync("factorio-test", Arg.Any<CancellationToken>()).Returns(Result<bool>.Success(true));
        ops.IsActiveAsync("mc-1", Arg.Any<CancellationToken>()).Returns(Result<bool>.Success(false));

        var fx = await Fixtures.ResolveAsync(inv, ops, default);

        fx.Instances.Should().HaveCount(2);
        fx.RunningCount.Should().Be(1);
        // Both games are unique; the running one wins, and ".bp" is stripped for the spoken word.
        fx.UniqueGameWord.Should().Be("factorio");
        fx.UniqueGameInstance.Should().Be("factorio-test");
        fx.RunningInstance.Should().Be("factorio-test");
        fx.StoppedInstance.Should().Be("mc-1");
        // minecraft IS installed (mc-1) despite the ".bp" suffix mismatch — never_game must skip it.
        fx.NeverInstalledGame.Should().Be("valheim");
        fx.Has(FixtureRole.MultipleInstances).Should().BeTrue();
    }

    [Fact]
    public async Task Empty_inventory_aborts_preflight()
    {
        var inv = Inventory(new Dictionary<string, string>(), "factorio");
        var ops = Substitute.For<IServerOperations>();

        var fx = await Fixtures.ResolveAsync(inv, ops, default);

        Fixtures.Preflight(fx, new StringWriter()).Should().BeFalse();
        fx.Has(FixtureRole.UniqueGame).Should().BeFalse();
    }

    [Fact]
    public async Task Fill_substitutes_placeholders()
    {
        var inv = Inventory(new Dictionary<string, string> { ["factorio-test"] = "factorio.bp" }, "minecraft");
        var ops = Substitute.For<IServerOperations>();
        ops.IsActiveAsync("factorio-test", Arg.Any<CancellationToken>()).Returns(Result<bool>.Success(true));

        var fx = await Fixtures.ResolveAsync(inv, ops, default);

        fx.Fill("is my {unique_game} server up? set up {never_game}")
            .Should().Be("is my factorio server up? set up minecraft");
    }
}
