using FluentAssertions;

using TheKrystalShip.Kgsm.Assistant.Search;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// Resolving which game a query is about, and which documents that admits. Pure string policy —
/// the retrieval adapter applies it, and <c>RagRetrievalGameScopeTests</c> covers that wiring.
/// </summary>
public sealed class GameScopeTests
{
    private static readonly string[] Games =
        ["factorio", "valheim", "palworld", "killingfloor", "killingfloor2", "7dtd",
         "projectzomboid", "theforest"];

    [Theory]
    [InlineData("my factorio server is down", "factorio")]
    [InlineData("FACTORIO won't start", "factorio")]
    [InlineData("how do I set up Valheim?", "valheim")]
    [InlineData("7dtd ports", "7dtd")]
    public void Resolves_a_single_named_game(string query, string expected) =>
        GameScope.Resolve(query, Games).Should().Be(expected);

    [Theory]
    [InlineData("my server is lagging")]
    [InlineData("what is the best cpu for a game server")]
    [InlineData("compare factorio and valheim")]
    public void Resolves_nothing_when_the_query_names_no_single_game(string query) =>
        GameScope.Resolve(query, Games).Should().BeNull();

    [Fact]
    public void Matches_whole_words_so_a_longer_name_is_not_matched_by_its_prefix() =>
        GameScope.Resolve("killingfloor2 admin setup", Games).Should().Be("killingfloor2");

    /// <summary>
    /// A blueprint name is one concatenated token; people type the words apart. Both spellings have
    /// to resolve, or scoping is silently inert for every game whose name reads as several words.
    /// </summary>
    [Theory]
    [InlineData("how do I set up a project zomboid server", "projectzomboid")]
    [InlineData("projectzomboid ports", "projectzomboid")]
    [InlineData("Project Zomboid admin password", "projectzomboid")]
    [InlineData("project-zomboid mods", "projectzomboid")]
    [InlineData("my the forest server won't start", "theforest")]
    public void Resolves_a_name_whose_words_are_written_apart(string query, string expected) =>
        GameScope.Resolve(query, Games).Should().Be(expected);

    [Fact]
    public void Separator_tolerance_does_not_let_a_prefix_swallow_a_longer_name() =>
        GameScope.Resolve("killing floor 2 admin setup", Games).Should().Be("killingfloor2");

    [Fact]
    public void Two_spellings_of_the_same_game_are_not_ambiguous() =>
        GameScope.Resolve("project zomboid and projectzomboid", Games).Should().Be("projectzomboid");

    [Fact]
    public void Resolves_nothing_without_a_vocabulary() =>
        GameScope.Resolve("my factorio server is down", []).Should().BeNull();

    [Theory]
    [InlineData("docs/knowledge/games/factorio/setup.md", "factorio")]
    [InlineData("/opt/kgsm/docs/knowledge/games/valheim/troubleshooting.md", "valheim")]
    [InlineData("docs/knowledge/native-server-launch.md", null)]
    [InlineData("docs/knowledge/games/README.md", null)]
    [InlineData("", null)]
    public void Reads_the_game_a_document_belongs_to(string path, string? expected) =>
        GameScope.GameOf(path).Should().Be(expected);

    [Fact]
    public void A_null_scope_admits_every_document() =>
        GameScope.Admits("docs/knowledge/games/factorio/setup.md", null).Should().BeTrue();

    [Fact]
    public void A_scope_admits_its_own_game_and_the_game_neutral_docs()
    {
        GameScope.Admits("docs/knowledge/games/factorio/setup.md", "factorio").Should().BeTrue();
        GameScope.Admits("docs/knowledge/native-server-launch.md", "factorio").Should().BeTrue();
    }

    [Fact]
    public void A_scope_excludes_another_games_docs() =>
        GameScope.Admits("docs/knowledge/games/valheim/setup.md", "factorio").Should().BeFalse();
}
