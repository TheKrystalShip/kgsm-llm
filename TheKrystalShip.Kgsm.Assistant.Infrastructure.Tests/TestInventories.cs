using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Ports;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Tests;

/// <summary>
/// Inventory stubs for retrieval tests. Retrieval consults the blueprint list only to learn which
/// game names exist, so a test states that vocabulary and nothing else.
/// </summary>
internal static class TestInventories
{
    /// <summary>An inventory that knows no games — nothing is scoped, so the whole index competes.</summary>
    public static IServerInventory NoGames() => WithGames();

    /// <summary>An inventory offering exactly <paramref name="names"/> as installable games.</summary>
    public static IServerInventory WithGames(params string[] names)
    {
        var inventory = Substitute.For<IServerInventory>();
        inventory.GetBlueprintNamesAsync(Arg.Any<CancellationToken>()).Returns(names);
        return inventory;
    }

    /// <summary>An inventory whose read throws — retrieval must degrade to unscoped, not fail.</summary>
    public static IServerInventory Unavailable()
    {
        var inventory = Substitute.For<IServerInventory>();
        inventory.GetBlueprintNamesAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyCollection<string>>(_ => throw new InvalidOperationException("kgsm unreachable"));
        return inventory;
    }
}
