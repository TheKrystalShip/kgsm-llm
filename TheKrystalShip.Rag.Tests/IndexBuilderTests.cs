using FluentAssertions;

using TheKrystalShip.Rag.Index;
using TheKrystalShip.Rag.Indexing;
using TheKrystalShip.Rag.Models;
using TheKrystalShip.Rag.Ollama;

namespace TheKrystalShip.Rag.Tests;

/// <summary>
/// The indexing engine over real files on disk with a recording fake embedder (no model in the
/// loop). Covers the full build, the incremental reuse win (D8), and — the two reuse-invalidation
/// guards an all-constant-params happy path would never exercise — a changed chunk size and a
/// changed embedding dimension forcing a clean full rebuild.
/// </summary>
public sealed class IndexBuilderTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "kgsm-rag-builder-" + Guid.NewGuid().ToString("N"));
    private readonly string _indexPath;

    public IndexBuilderTests()
    {
        Directory.CreateDirectory(_dir);
        _indexPath = Path.Combine(_dir, "index.krag");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task A_full_build_writes_a_readable_index_stamped_with_model_and_dimension()
    {
        WriteFile("a.md", "alpha unique marker content");
        WriteFile("b.md", "bravo unique marker content");
        var embedder = new RecordingEmbeddingClient(dimension: 4);

        var result = await BuildAsync(embedder);

        result.IsSuccess.Should().BeTrue(because: result.Error);
        result.Value!.SourceFiles.Should().Be(2);
        result.Value!.FilesReused.Should().Be(0);

        var index = RagIndexFile.ReadFromFile(_indexPath);
        index.EmbeddingModel.Should().Be("embeddinggemma");
        index.Dimension.Should().Be(4);
        index.Chunks.Should().HaveCount(2);
        index.Manifest.Should().HaveCount(2);
        index.Chunks.Select(c => c.Vector.Length).Should().AllBeEquivalentTo(4);
    }

    [Fact]
    public async Task Non_matching_files_are_ignored_by_the_pattern()
    {
        WriteFile("keep.md", "indexed content");
        WriteFile("skip.txt", "ignored content");
        var embedder = new RecordingEmbeddingClient(dimension: 4);

        var result = await BuildAsync(embedder);

        result.Value!.SourceFiles.Should().Be(1);
        RagIndexFile.ReadFromFile(_indexPath).Manifest.Single().Path.Should().EndWith("keep.md");
    }

    [Fact]
    public async Task An_incremental_rebuild_reuses_unchanged_files_and_re_embeds_only_the_changed_one()
    {
        WriteFile("a.md", "alpha unique marker content");
        WriteFile("b.md", "bravo unique marker content");
        var embedder = new RecordingEmbeddingClient(dimension: 4);
        (await BuildAsync(embedder)).IsSuccess.Should().BeTrue();
        var previous = RagIndexFile.ReadFromFile(_indexPath);

        embedder.Embedded.Clear();
        WriteFile("b.md", "bravo CHANGED marker content");
        var result = await BuildAsync(embedder, previous: previous);

        result.IsSuccess.Should().BeTrue(because: result.Error);
        result.Value!.FilesReused.Should().Be(1);
        result.Value!.FilesEmbedded.Should().Be(1);
        embedder.Embedded.Should().Contain(d => d.Contains("bravo"), "the changed file is re-embedded");
        embedder.Embedded.Should().NotContain(d => d.Contains("alpha"), "the unchanged file is reused, not re-embedded");
    }

    [Fact]
    public async Task A_removed_source_file_is_dropped_from_the_rebuilt_index()
    {
        WriteFile("a.md", "alpha content");
        WriteFile("b.md", "bravo content");
        var embedder = new RecordingEmbeddingClient(dimension: 4);
        (await BuildAsync(embedder)).IsSuccess.Should().BeTrue();
        var previous = RagIndexFile.ReadFromFile(_indexPath);

        File.Delete(Path.Combine(_dir, "b.md"));
        var result = await BuildAsync(embedder, previous: previous);

        result.Value!.FilesRemoved.Should().Be(1);
        var index = RagIndexFile.ReadFromFile(_indexPath);
        index.Manifest.Should().ContainSingle().Which.Path.Should().EndWith("a.md");
    }

    [Fact]
    public async Task A_changed_chunk_size_invalidates_reuse_and_re_embeds_everything()
    {
        WriteFile("a.md", "alpha content");
        WriteFile("b.md", "bravo content");
        var embedder = new RecordingEmbeddingClient(dimension: 4);
        (await BuildAsync(embedder, chunkSize: 2000)).IsSuccess.Should().BeTrue();
        var previous = RagIndexFile.ReadFromFile(_indexPath);

        embedder.Embedded.Clear();
        var result = await BuildAsync(embedder, previous: previous, chunkSize: 500);

        result.IsSuccess.Should().BeTrue(because: result.Error);
        result.Value!.FilesReused.Should().Be(0, "different chunk params mean the carried-over chunks would be split differently");
        result.Value!.FilesEmbedded.Should().Be(2);
    }

    [Fact]
    public async Task A_changed_embedding_dimension_invalidates_reuse_and_rebuilds_cleanly()
    {
        WriteFile("a.md", "alpha content");
        var build1 = await BuildAsync(new RecordingEmbeddingClient(dimension: 4));
        build1.IsSuccess.Should().BeTrue();
        var previous = RagIndexFile.ReadFromFile(_indexPath);
        previous.Dimension.Should().Be(4);

        // Same model tag, different dimension (the pathological silent-dim-change case): reuse must
        // be rejected and the whole index rebuilt at the new dimension — never a mixed-dim write.
        var result = await BuildAsync(new RecordingEmbeddingClient(dimension: 8), previous: previous);

        result.IsSuccess.Should().BeTrue(because: result.Error);
        result.Value!.FilesReused.Should().Be(0);
        RagIndexFile.ReadFromFile(_indexPath).Dimension.Should().Be(8);
    }

    [Fact]
    public async Task A_mid_build_embedder_failure_fails_closed_without_writing_an_index()
    {
        WriteFile("a.md", "alpha content");
        var result = await BuildAsync(new FailAfterProbeEmbeddingClient());

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("backend down mid-build");
        File.Exists(_indexPath).Should().BeFalse("a failed build must not leave a partial index");
    }

    [Fact]
    public async Task No_matching_sources_is_a_failure()
    {
        var result = await BuildAsync(new RecordingEmbeddingClient(dimension: 4)); // empty dir

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("no source files");
    }

    [Fact]
    public async Task Live_build_against_real_ollama_produces_a_readable_index()
    {
        if (Environment.GetEnvironmentVariable("KGSM_LIVE_OLLAMA") != "1")
            return; // gated, mirrors the Phase 1 smoke

        var model = Environment.GetEnvironmentVariable("KGSM_LIVE_EMBED_MODEL") ?? "embeddinggemma";
        WriteFile("kgsm.md", "# KGSM\n\nKGSM manages game servers through a stateless CLI.\n");
        var embedder = new OllamaEmbeddingClient(
            Microsoft.Extensions.Options.Options.Create(new RagEmbeddingOptions { EmbeddingModel = model }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OllamaEmbeddingClient>.Instance);
        var builder = new IndexBuilder(embedder);

        var result = await builder.BuildAsync(
            new IndexBuilderOptions { Sources = [_dir] }, _indexPath);

        result.IsSuccess.Should().BeTrue(because: result.Error);
        var index = RagIndexFile.ReadFromFile(_indexPath);
        index.Chunks.Should().NotBeEmpty();
        index.Dimension.Should().BeGreaterThan(0);
        index.Chunks.Select(c => c.Vector.Length).Should().AllBeEquivalentTo(index.Dimension);
    }

    private Task<Result<IndexBuildReport>> BuildAsync(
        IEmbeddingClient embedder, RagIndex? previous = null, int chunkSize = 2000)
    {
        var builder = new IndexBuilder(embedder);
        var options = new IndexBuilderOptions { Sources = [_dir], ChunkSize = chunkSize };
        return builder.BuildAsync(options, _indexPath, previous);
    }

    private void WriteFile(string name, string content) => File.WriteAllText(Path.Combine(_dir, name), content);
}

/// <summary>Fake embedder: records every embedded input and returns deterministic non-zero vectors of a fixed dimension.</summary>
internal sealed class RecordingEmbeddingClient(string model = "embeddinggemma", int dimension = 4) : IEmbeddingClient
{
    public string ModelName { get; } = model;
    public List<string> Embedded { get; } = [];

    public Task<Result<IReadOnlyList<float[]>>> EmbedDocumentsAsync(
        IReadOnlyList<string> documents, CancellationToken cancellationToken = default)
    {
        Embedded.AddRange(documents);
        IReadOnlyList<float[]> vectors = documents.Select(Vector).ToArray();
        return Task.FromResult(Result.Success(vectors));
    }

    public Task<Result<float[]>> EmbedQueryAsync(string query, CancellationToken cancellationToken = default)
        => Task.FromResult(Result.Success(Vector(query)));

    private float[] Vector(string text)
    {
        var v = new float[dimension];
        for (var i = 0; i < dimension; i++)
            v[i] = (text.Length + i) % 5 + 1; // deterministic, never all-zero
        return v;
    }
}

/// <summary>Succeeds for the dimension probe (first call) then fails — exercises the per-file embed failure path.</summary>
internal sealed class FailAfterProbeEmbeddingClient : IEmbeddingClient
{
    private int _calls;
    public string ModelName => "embeddinggemma";

    public Task<Result<IReadOnlyList<float[]>>> EmbedDocumentsAsync(
        IReadOnlyList<string> documents, CancellationToken cancellationToken = default)
    {
        if (++_calls == 1)
        {
            IReadOnlyList<float[]> probe = documents.Select(_ => new float[] { 1, 1, 1, 1 }).ToArray();
            return Task.FromResult(Result.Success(probe));
        }
        return Task.FromResult(Result.Failure<IReadOnlyList<float[]>>("backend down mid-build"));
    }

    public Task<Result<float[]>> EmbedQueryAsync(string query, CancellationToken cancellationToken = default)
        => Task.FromResult(Result.Failure<float[]>("not used"));
}
