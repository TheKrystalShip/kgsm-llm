using FluentAssertions;

using TheKrystalShip.Api.Contracts;

using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Kgsm.Assistant.Service.Cluster;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

/// <summary>
/// Turning a node's catalog row into the game description the assistant speaks from.
/// </summary>
/// <remarks>
/// The two adapters behind the inventory port answer the same questions from different places — the
/// engine beside this machine, or every node's Control Panel API — and a model reads whichever
/// answered without being told which. So what a game is called, what ports it wants and what it can
/// do to a player have to come out the same shape either way; the cases here are the ones where the
/// two sources spell one fact differently.
/// </remarks>
public sealed class ClusterCatalogTests
{
    private static LibraryEntry Row(
        string id,
        string? name = null,
        ModerationCapability? moderation = null,
        IReadOnlyList<LibraryPort>? ports = null,
        LibrarySpecs? specs = null,
        string? description = null) =>
        new(id, name ?? id, "native", null, null, false,
            ports ?? [], specs ?? new LibrarySpecs(null, null, null, null),
            null, null, description, [], [], null,
            moderation ?? new ModerationCapability(false, false, false, null));

    /// <summary>
    /// A node answers the id when a game declares no display name, and this shape carries the absence
    /// — so the fallback happens once, where a label is read, instead of a game appearing to be called
    /// after its own slug.
    /// </summary>
    [Fact]
    public void A_game_named_after_its_own_id_has_no_display_name()
    {
        BlueprintDetail detail = ClusterCatalog.Describe(Row("factorio"))!;

        detail.Name.Should().Be("factorio");
        detail.DisplayName.Should().BeNull();
    }

    [Fact]
    public void A_curated_name_is_carried_as_the_display_name()
    {
        BlueprintDetail detail = ClusterCatalog.Describe(Row("lotrrtm", "The Lord of the Rings: Return to Moria"))!;

        detail.DisplayName.Should().Be("The Lord of the Rings: Return to Moria");
    }

    /// <summary>
    /// The verbs are what stops the assistant offering to ban somebody on a game that cannot, so an
    /// action a node did not declare must not appear here under any spelling.
    /// </summary>
    [Fact]
    public void Only_the_declared_moderation_actions_become_verbs()
    {
        BlueprintDetail detail =
            ClusterCatalog.Describe(Row("minecraft", moderation: new ModerationCapability(true, true, false, "name")))!;

        detail.ModerationVerbs.Should().Equal("kick", "ban");
    }

    [Fact]
    public void A_game_that_declares_no_moderation_supports_none()
    {
        ClusterCatalog.Describe(Row("factorio"))!.ModerationVerbs.Should().BeEmpty();
        ClusterCatalog.Describe(Row("factorio", moderation: new ModerationCapability(false, false, false, null)))!
            .ModerationVerbs.Should().BeEmpty();
    }

    [Fact]
    public void Ports_read_as_the_ecosystem_writes_them()
    {
        BlueprintDetail detail = ClusterCatalog.Describe(Row("7dtd", ports:
        [
            new LibraryPort(26900, 26903, "tcp"),
            new LibraryPort(27015, 27015, "udp"),
        ]))!;

        detail.Ports.Should().Equal("26900:26903/tcp", "27015/udp");
    }

    /// <summary>
    /// A figure a game does not declare is unknown, and an unknown figure stays null the whole way
    /// through — a zero here would be read as "this game needs no memory".
    /// </summary>
    [Fact]
    public void An_undeclared_figure_stays_unknown()
    {
        BlueprintDetail sparse = ClusterCatalog.Describe(Row("factorio"))!;
        sparse.MaxPlayers.Should().BeNull();
        sparse.MinRamMb.Should().BeNull();
        sparse.RecommendedRamMb.Should().BeNull();
        sparse.BaseDiskMb.Should().BeNull();

        BlueprintDetail curated =
            ClusterCatalog.Describe(Row("factorio", specs: new LibrarySpecs(64, 2048, 4096, null)))!;
        curated.MaxPlayers.Should().Be(64);
        curated.MinRamMb.Should().Be(2048);
        curated.RecommendedRamMb.Should().Be(4096);
        curated.BaseDiskMb.Should().BeNull();
    }

    [Fact]
    public void A_blank_description_is_absent_rather_than_empty()
    {
        ClusterCatalog.Describe(Row("factorio", description: "   "))!.Description.Should().BeNull();
        ClusterCatalog.Describe(Row("factorio", description: "Build a factory."))!
            .Description.Should().Be("Build a factory.");
    }

    [Fact]
    public void No_game_describes_to_nothing()
    {
        ClusterCatalog.Describe(null).Should().BeNull();
    }
}
