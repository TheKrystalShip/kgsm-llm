using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Infrastructure.Configuration;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Retrieval;
using TheKrystalShip.Rag.Index;
using TheKrystalShip.Rag.Models;
using TheKrystalShip.Rag.Ollama;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Tests;

/// <summary>
/// The retrieval adapter over a canned on-disk index, with a stubbed embedder (no model in the
/// loop). Verifies cosine top-k ordering + field mapping on the happy path, and the fail-closed
/// (never-throw) behaviour the port contract and §D7 demand: blank query, embedder down, a
/// dimension that disagrees with the index, and a missing index.
/// </summary>
public sealed class RagRetrievalTests : IDisposable
{
    private const string Model = "embeddinggemma";
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "kgsm-rag-retrieval-" + Guid.NewGuid().ToString("N"));

    public RagRetrievalTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Returns_chunks_ordered_by_cosine_similarity_with_fields_mapped()
    {
        // a=[1,0,0] b=[0,1,0] c=[1,1,0]; query [1,0,0] → a:1.0, c:~0.707, b:0.
        var index = IndexWith(("a.md", [1, 0, 0]), ("b.md", [0, 1, 0]), ("c.md", [1, 1, 0]));
        var retrieval = RetrievalOver(index, queryVector: [1, 0, 0], topK: 2);

        var result = await retrieval.RetrieveAsync("anything");

        result.IsSuccess.Should().BeTrue(because: result.Error);
        result.Value!.Select(c => c.SourcePath).Should().Equal("a.md", "c.md");
        var top = result.Value![0];
        top.Score.Should().BeApproximately(1.0, 1e-6);
        top.HeaderPath.Should().Be("Doc");
        top.Text.Should().Be("chunk a.md");
    }

    [Fact]
    public async Task A_blank_query_fails_closed_without_calling_the_embedder()
    {
        var embeddings = Substitute.For<IEmbeddingClient>();
        embeddings.ModelName.Returns(Model);
        var retrieval = RetrievalOver(IndexWith(("a.md", [1, 0, 0])), embeddings);

        var result = await retrieval.RetrieveAsync("   ");

        result.IsFailure.Should().BeTrue();
        await embeddings.DidNotReceive().EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_embedder_failure_is_surfaced_as_a_failed_result()
    {
        var embeddings = Substitute.For<IEmbeddingClient>();
        embeddings.ModelName.Returns(Model);
        embeddings.EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<float[]>("Could not reach the embedding backend."));
        var retrieval = RetrievalOver(IndexWith(("a.md", [1, 0, 0])), embeddings);

        var result = await retrieval.RetrieveAsync("anything");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("embedding backend");
    }

    [Fact]
    public async Task A_query_vector_whose_dimension_disagrees_with_the_index_fails_closed()
    {
        // Index dim 3, but the embedder hands back a 4-d vector — VectorSearch would throw; we must not.
        var retrieval = RetrievalOver(IndexWith(("a.md", [1, 0, 0])), queryVector: [1, 0, 0, 0], topK: 2);

        var result = await retrieval.RetrieveAsync("anything");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("dimension");
    }

    [Fact]
    public async Task A_missing_index_fails_closed()
    {
        var embeddings = Substitute.For<IEmbeddingClient>();
        embeddings.ModelName.Returns(Model);
        var provider = new RagIndexProvider(
            Options.Create(new RagOptions { Enabled = true, IndexPath = Path.Combine(_dir, "absent.krag") }),
            embeddings, NullLogger<RagIndexProvider>.Instance);
        var retrieval = new RagRetrieval(
            embeddings, provider,
            Options.Create(new RagOptions { TopK = 5 }), NullLogger<RagRetrieval>.Instance);

        var result = await retrieval.RetrieveAsync("anything");

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task MinScore_drops_hits_below_the_floor()
    {
        var index = IndexWith(("a.md", [1, 0, 0]), ("c.md", [1, 1, 0]));
        // query [1,0,0]: a=1.0, c=~0.707. Floor 0.8 keeps only a.
        var retrieval = RetrievalOver(index, queryVector: [1, 0, 0], topK: 5, minScore: 0.8);

        var result = await retrieval.RetrieveAsync("anything");

        result.IsSuccess.Should().BeTrue(because: result.Error);
        result.Value!.Select(c => c.SourcePath).Should().Equal("a.md");
    }

    // --- helpers ---------------------------------------------------------------------------------

    private RagRetrieval RetrievalOver(
        RagIndex index, float[] queryVector, int topK = 5, double minScore = 0.0)
    {
        var embeddings = Substitute.For<IEmbeddingClient>();
        embeddings.ModelName.Returns(Model);
        embeddings.EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(queryVector));
        return RetrievalOver(index, embeddings, topK, minScore);
    }

    private RagRetrieval RetrievalOver(RagIndex index, IEmbeddingClient embeddings, int topK = 5, double minScore = 0.0)
    {
        var path = Path.Combine(_dir, "index.krag");
        RagIndexFile.WriteToFile(path, index);
        var provider = new RagIndexProvider(
            Options.Create(new RagOptions { Enabled = true, IndexPath = path }),
            embeddings, NullLogger<RagIndexProvider>.Instance);
        return new RagRetrieval(
            embeddings, provider,
            Options.Create(new RagOptions { TopK = topK, MinScore = minScore }),
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
