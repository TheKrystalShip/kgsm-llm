using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Infrastructure.Configuration;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Retrieval;
using TheKrystalShip.Rag.Index;
using TheKrystalShip.Rag.Indexing;
using TheKrystalShip.Rag.Models;
using TheKrystalShip.Rag.Embedding;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Tests;

/// <summary>
/// The phase's literal success criterion: an index the Phase 3a engine produces is consumed by the
/// Phase 2 retrieval stack — through the §D9 model-match check in <see cref="RagIndexProvider"/> and
/// the dimension guard + field mapping in <see cref="RagRetrieval"/>, not just the format reader the
/// builder's own smoke exercises. Same embedder both ends (deterministic vectors), so a query that
/// repeats a chunk's text retrieves that chunk.
/// </summary>
public sealed class RagIndexConsumesBuilderOutputTests : IDisposable
{
    private const string Model = "embeddinggemma";
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "kgsm-rag-e2e-" + Guid.NewGuid().ToString("N"));

    public RagIndexConsumesBuilderOutputTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task A_builder_produced_index_is_loaded_and_retrieved_by_the_phase_2_stack()
    {
        File.WriteAllText(Path.Combine(_dir, "kgsm.md"), "alpha content about kgsm game servers");
        var indexPath = Path.Combine(_dir, "index.krag");
        var embedder = new DeterministicEmbedder(Model, dimension: 4);

        var build = await new IndexBuilder(embedder).BuildAsync(
            new IndexBuilderOptions { Sources = [_dir] }, indexPath);
        build.IsSuccess.Should().BeTrue(because: build.Error);

        var provider = new RagIndexProvider(
            Options.Create(new RagOptions { Enabled = true, IndexPath = indexPath }),
            embedder, NullLogger<RagIndexProvider>.Instance);
        var retrieval = new RagRetrieval(
            embedder, provider, TestInventories.NoGames(),
            Options.Create(new RagOptions { TopK = 5 }), NullLogger<RagRetrieval>.Instance);

        // Querying with a produced chunk's exact text → identical vector → that chunk is the top hit.
        var producedChunk = RagIndexFile.ReadFromFile(indexPath).Chunks[0].Text;
        var result = await retrieval.RetrieveAsync(producedChunk);

        result.IsSuccess.Should().BeTrue(because: result.Error);
        result.Value!.Should().NotBeEmpty();
        result.Value![0].Text.Should().Be(producedChunk);
    }

    /// <summary>Deterministic fake: same text → same vector on both the document and query paths.</summary>
    private sealed class DeterministicEmbedder(string model, int dimension) : IEmbeddingClient
    {
        public string ModelName => model;

        public Task<Result<IReadOnlyList<float[]>>> EmbedDocumentsAsync(
            IReadOnlyList<string> documents, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<float[]> vectors = documents.Select(Vector).ToArray();
            return Task.FromResult(Result.Success(vectors));
        }

        public Task<Result<float[]>> EmbedQueryAsync(string query, CancellationToken cancellationToken = default)
            => Task.FromResult(Result.Success(Vector(query)));

        private float[] Vector(string text)
        {
            var v = new float[dimension];
            for (var i = 0; i < dimension; i++)
                v[i] = (text.Length + i) % 5 + 1;
            return v;
        }
    }
}
