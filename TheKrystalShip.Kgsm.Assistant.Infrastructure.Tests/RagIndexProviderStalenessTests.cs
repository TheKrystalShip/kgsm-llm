using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Infrastructure.Configuration;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Retrieval;
using TheKrystalShip.Rag.Embedding;
using TheKrystalShip.Rag.Index;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Tests;

/// <summary>
/// Answering from a previously loaded index is a state a success cannot express.
/// </summary>
/// <remarks>
/// ⚠ <b>Measured on the live host, not imagined.</b> The index was moved aside and the provider went on
/// returning success — <c>"Reload of the changed retrieval index failed; continuing with the previously
/// loaded index"</c> — so a health reading that only checked <c>IsSuccess</c> saw nothing wrong. That is
/// the case the whole component exists for: retrieval works, and answers from a corpus that is no longer
/// the one on disk.
/// </remarks>
public sealed class RagIndexProviderStalenessTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "kgsm-rag-staleness", Path.GetRandomFileName());

    private string IndexPath => Path.Combine(_dir, "rag-index.krag");

    public RagIndexProviderStalenessTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    private const string Model = "test-model";

    private RagIndexProvider Create()
    {
        var embeddings = Substitute.For<IEmbeddingClient>();
        embeddings.ModelName.Returns(Model);
        return new RagIndexProvider(
            Options.Create(new RagOptions { Enabled = true, IndexPath = IndexPath }),
            embeddings, NullLogger<RagIndexProvider>.Instance);
    }

    private void WriteIndex() =>
        RagIndexFile.WriteToFile(IndexPath, new RagIndex
        {
            EmbeddingModel = Model,
            Dimension = 2,
            ChunkSize = 2000,
            ChunkOverlap = 200,
            Chunks = [new IndexedChunk("a.md", "Doc", "hello", [1f, 0f])],
            Manifest = [new SourceFileEntry("a.md", "hash", 0, 1)],
        });

    [Fact]
    public void AFreshlyLoadedIndex_IsNotStale()
    {
        WriteIndex();
        RagIndexProvider provider = Create();

        provider.Get().IsSuccess.Should().BeTrue();
        provider.ServingLastGoodBecause.Should().BeNull();
    }

    /// <summary>
    /// ⚠ The index is gone, retrieval still succeeds, and that is exactly the point.
    /// </summary>
    [Fact]
    public void AnIndexThatVanished_StillSucceeds_ButSaysWhyItIsStale()
    {
        WriteIndex();
        RagIndexProvider provider = Create();
        provider.Get().IsSuccess.Should().BeTrue();

        File.Delete(IndexPath);

        provider.Get().IsSuccess.Should().BeTrue("going dark would be worse than serving the last good one");
        provider.ServingLastGoodBecause.Should().NotBeNullOrEmpty(
            "a caller reporting this leaf's health cannot tell from the result that the corpus is stale");
    }

    /// <summary>A good index coming back clears the staleness rather than latching it.</summary>
    [Fact]
    public void AnIndexThatCameBack_IsNoLongerStale()
    {
        WriteIndex();
        RagIndexProvider provider = Create();
        provider.Get();

        File.Delete(IndexPath);
        provider.Get();
        provider.ServingLastGoodBecause.Should().NotBeNull();

        WriteIndex();
        provider.Get().IsSuccess.Should().BeTrue();
        provider.ServingLastGoodBecause.Should().BeNull();
    }
}
