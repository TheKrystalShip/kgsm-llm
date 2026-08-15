using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Infrastructure.Configuration;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Retrieval;
using TheKrystalShip.Rag.Index;
using TheKrystalShip.Rag.Models;
using TheKrystalShip.Rag.Embedding;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Tests;

/// <summary>
/// Game scoping in the retrieval adapter: a query naming one known game competes only against that
/// game's documents plus the game-neutral ones. The vectors here are deliberately rigged so the
/// WRONG game's document is the best match — that is the failure being prevented, since a
/// confident local hit suppresses the web fallback that would have answered correctly.
/// </summary>
public sealed class RagRetrievalGameScopeTests : IDisposable
{
    private const string Model = "embeddinggemma";
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "kgsm-rag-scope-" + Guid.NewGuid().ToString("N"));

    public RagRetrievalGameScopeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task A_query_naming_an_undocumented_game_retrieves_nothing_from_another_games_docs()
    {
        var retrieval = RetrievalOver(
            IndexWith(
                ("docs/knowledge/games/factorio/troubleshooting.md", [1, 0, 0]),
                ("docs/knowledge/games/valheim/troubleshooting.md", [0, 1, 0])),
            queryVector: [1, 0, 0],
            games: ["factorio", "valheim", "palworld"]);

        // Palworld has no documents; the factorio chunk is the nearest vector but must not answer.
        var result = await retrieval.RetrieveAsync("my palworld server is lagging");

        result.IsSuccess.Should().BeTrue(because: result.Error);
        result.Value!.Should().BeEmpty();
    }

    [Fact]
    public async Task A_query_naming_a_documented_game_retrieves_only_that_games_docs()
    {
        var retrieval = RetrievalOver(
            IndexWith(
                ("docs/knowledge/games/factorio/troubleshooting.md", [0, 1, 0]),
                ("docs/knowledge/games/valheim/troubleshooting.md", [1, 0, 0])),
            queryVector: [1, 0, 0],
            games: ["factorio", "valheim"]);

        // The valheim chunk is the closer vector, but the query is about factorio.
        var result = await retrieval.RetrieveAsync("my factorio server will not start");

        result.IsSuccess.Should().BeTrue(because: result.Error);
        result.Value!.Select(c => c.SourcePath)
            .Should().ContainSingle().Which.Should().Contain("factorio");
    }

    [Fact]
    public async Task Game_neutral_docs_stay_eligible_under_a_scope()
    {
        var retrieval = RetrievalOver(
            IndexWith(
                ("docs/knowledge/native-server-launch.md", [1, 0, 0]),
                ("docs/knowledge/games/valheim/setup.md", [1, 0, 0])),
            queryVector: [1, 0, 0],
            games: ["factorio", "valheim"]);

        var result = await retrieval.RetrieveAsync("how do I set up factorio");

        result.IsSuccess.Should().BeTrue(because: result.Error);
        result.Value!.Select(c => c.SourcePath).Should().Equal("docs/knowledge/native-server-launch.md");
    }

    [Fact]
    public async Task A_query_naming_no_game_leaves_the_whole_corpus_eligible()
    {
        var retrieval = RetrievalOver(
            IndexWith(
                ("docs/knowledge/games/factorio/setup.md", [1, 0, 0]),
                ("docs/knowledge/games/valheim/setup.md", [0, 1, 0])),
            queryVector: [1, 0, 0],
            games: ["factorio", "valheim"]);

        var result = await retrieval.RetrieveAsync("how do I back up a world");

        result.IsSuccess.Should().BeTrue(because: result.Error);
        result.Value!.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_query_naming_two_games_is_ambiguous_and_scopes_nothing()
    {
        var retrieval = RetrievalOver(
            IndexWith(
                ("docs/knowledge/games/factorio/setup.md", [1, 0, 0]),
                ("docs/knowledge/games/valheim/setup.md", [0, 1, 0])),
            queryVector: [1, 0, 0],
            games: ["factorio", "valheim"]);

        var result = await retrieval.RetrieveAsync("is factorio or valheim heavier to host");

        result.IsSuccess.Should().BeTrue(because: result.Error);
        result.Value!.Should().HaveCount(2);
    }

    [Fact]
    public async Task An_unavailable_inventory_degrades_to_unscoped_retrieval()
    {
        var retrieval = RetrievalOver(
            IndexWith(("docs/knowledge/games/factorio/setup.md", [1, 0, 0])),
            queryVector: [1, 0, 0],
            inventory: TestInventories.Unavailable());

        // Naming a game must not fail the search just because the blueprint list could not be read.
        var result = await retrieval.RetrieveAsync("my palworld server is lagging");

        result.IsSuccess.Should().BeTrue(because: result.Error);
        result.Value!.Should().ContainSingle();
    }

    [Fact]
    public async Task The_blueprint_list_is_read_once_across_repeated_searches()
    {
        var inventory = TestInventories.WithGames("factorio");
        var retrieval = RetrievalOver(
            IndexWith(("docs/knowledge/games/factorio/setup.md", [1, 0, 0])),
            queryVector: [1, 0, 0],
            inventory: inventory);

        await retrieval.RetrieveAsync("factorio one");
        await retrieval.RetrieveAsync("factorio two");
        await retrieval.RetrieveAsync("factorio three");

        await inventory.Received(1).GetBlueprintNamesAsync(Arg.Any<CancellationToken>());
    }

    // --- helpers ---------------------------------------------------------------------------------

    private RagRetrieval RetrievalOver(
        RagIndex index, float[] queryVector, string[]? games = null,
        TheKrystalShip.Kgsm.Assistant.Ports.IServerInventory? inventory = null)
    {
        var embeddings = Substitute.For<IEmbeddingClient>();
        embeddings.ModelName.Returns(Model);
        embeddings.EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(queryVector));

        var path = Path.Combine(_dir, "index.krag");
        RagIndexFile.WriteToFile(path, index);
        var provider = new RagIndexProvider(
            Options.Create(new RagOptions { Enabled = true, IndexPath = path }),
            embeddings, NullLogger<RagIndexProvider>.Instance);

        return new RagRetrieval(
            embeddings, provider,
            inventory ?? TestInventories.WithGames(games ?? []),
            Options.Create(new RagOptions { TopK = 10 }),
            NullLogger<RagRetrieval>.Instance);
    }

    private static RagIndex IndexWith(params (string Path, float[] Vector)[] chunks) => new()
    {
        EmbeddingModel = Model,
        Dimension = chunks[0].Vector.Length,
        ChunkSize = 2000,
        ChunkOverlap = 200,
        Chunks = chunks.Select(c => new IndexedChunk(c.Path, "Doc", $"chunk {c.Path}", c.Vector)).ToArray(),
        Manifest = chunks.Select((c, i) => new SourceFileEntry(c.Path, "hash", i, 1)).ToArray(),
    };
}
