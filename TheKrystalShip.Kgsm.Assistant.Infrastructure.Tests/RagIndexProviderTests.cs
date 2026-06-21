using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Infrastructure.Configuration;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Retrieval;
using TheKrystalShip.Rag.Index;
using TheKrystalShip.Rag.Ollama;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Tests;

/// <summary>
/// The reader half of the §D9 decoupling contract: load a real <c>.krag</c> file, cache it, and
/// reject anything that does not match this host's configured embedder. All paths must fail
/// closed (a failed Result), never throw.
/// </summary>
public sealed class RagIndexProviderTests : IDisposable
{
    private const string Model = "embeddinggemma";
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "kgsm-rag-provider-" + Guid.NewGuid().ToString("N"));

    public RagIndexProviderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void A_matching_index_loads()
    {
        var path = WriteIndex(BuildIndex(Model, dimension: 3));
        var provider = ProviderFor(path, embedderModel: Model);

        var result = provider.Get();

        result.IsSuccess.Should().BeTrue(because: result.Error);
        result.Value!.EmbeddingModel.Should().Be(Model);
        result.Value!.Chunks.Should().HaveCount(1);
    }

    [Fact]
    public void An_unconfigured_path_fails_closed()
    {
        var provider = ProviderFor(indexPath: "", embedderModel: Model);

        var result = provider.Get();

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("no retrieval index path");
    }

    [Fact]
    public void A_missing_index_file_fails_closed_then_recovers_once_built()
    {
        var path = Path.Combine(_dir, "index.krag");
        var provider = ProviderFor(path, embedderModel: Model);

        // Expected pre-indexer state — fails closed, no throw.
        provider.Get().IsFailure.Should().BeTrue();

        // The indexer produces the file; the provider re-attempts and succeeds.
        WriteIndex(BuildIndex(Model, dimension: 3), path);
        provider.Get().IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void An_index_from_a_different_embedding_model_is_rejected()
    {
        var path = WriteIndex(BuildIndex("nomic-embed-text", dimension: 3));
        var provider = ProviderFor(path, embedderModel: Model);

        var result = provider.Get();

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("different embedding model");
    }

    [Fact]
    public void A_file_that_is_not_a_krag_index_is_rejected()
    {
        var path = Path.Combine(_dir, "garbage.krag");
        File.WriteAllText(path, "this is not a KRAG file");
        var provider = ProviderFor(path, embedderModel: Model);

        var result = provider.Get();

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("incompatible build");
    }

    [Fact]
    public void A_valid_header_over_a_corrupt_body_fails_closed_without_throwing()
    {
        // Valid "KRAG" magic + format version 1, then garbage where the model string's 7-bit
        // length prefix should be — BinaryReader throws a FormatException, OUTSIDE the IO family.
        // The never-throw contract still requires a failed Result, not an exception.
        var path = Path.Combine(_dir, "corrupt.krag");
        var bytes = new List<byte>();
        bytes.AddRange("KRAG"u8.ToArray());
        bytes.AddRange([1, 0, 0, 0]);                 // FormatVersion = 1 (little-endian int32)
        bytes.AddRange([0xFF, 0xFF, 0xFF, 0xFF, 0xFF]); // un-decodable 7-bit length prefix
        File.WriteAllBytes(path, bytes.ToArray());
        var provider = ProviderFor(path, embedderModel: Model);

        // A throw here (rather than a returned failure) fails the test — that is the contract.
        var result = provider.Get();

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("corrupt");
    }

    [Fact]
    public void A_successful_load_is_cached()
    {
        var path = WriteIndex(BuildIndex(Model, dimension: 3));
        var provider = ProviderFor(path, embedderModel: Model);

        provider.Get().IsSuccess.Should().BeTrue();

        // Delete the file: a cached provider must still serve the loaded index.
        File.Delete(path);
        provider.Get().IsSuccess.Should().BeTrue(because: "the first successful load is cached");
    }

    private RagIndexProvider ProviderFor(string indexPath, string embedderModel)
    {
        var embeddings = Substitute.For<IEmbeddingClient>();
        embeddings.ModelName.Returns(embedderModel);
        var options = Options.Create(new RagOptions { Enabled = true, IndexPath = indexPath });
        return new RagIndexProvider(options, embeddings, NullLogger<RagIndexProvider>.Instance);
    }

    private string WriteIndex(RagIndex index, string? path = null)
    {
        path ??= Path.Combine(_dir, "index.krag");
        RagIndexFile.WriteToFile(path, index);
        return path;
    }

    private static RagIndex BuildIndex(string model, int dimension) => new()
    {
        EmbeddingModel = model,
        Dimension = dimension,
        ChunkSize = 2000,
        ChunkOverlap = 200,
        Chunks = [new IndexedChunk("doc.md", "Doc", "hello world", UnitVector(dimension))],
        Manifest = [new SourceFileEntry("doc.md", "hash", 0, 1)],
    };

    private static float[] UnitVector(int dimension)
    {
        var v = new float[dimension];
        v[0] = 1f;
        return v;
    }
}
