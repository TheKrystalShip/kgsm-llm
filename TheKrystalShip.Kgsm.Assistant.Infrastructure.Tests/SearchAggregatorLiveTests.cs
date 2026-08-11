using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Configuration;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Retrieval;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Rag.Indexing;
using TheKrystalShip.Rag.Ollama;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Tests;

/// <summary>
/// End-to-end of the local-first path against a REAL embedder: build an index from a doc,
/// then resolve it through the real <see cref="RagRetrieval"/> and the <see cref="SearchAggregator"/>
/// — exactly the production wiring, minus the model deciding to call the tool. Gated on
/// <c>KGSM_LIVE_OLLAMA=1</c>, like the other live smokes. Also logs the real cosine top score so the
/// <c>LocalMinScore</c> default (0.35) can be sanity-checked against real embeddings; the assertion
/// is that the path works (threshold 0), not that the default is the right value.
/// </summary>
public sealed class SearchAggregatorLiveTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "kgsm-rag-search-live-" + Guid.NewGuid().ToString("N"));

    public SearchAggregatorLiveTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task A_relevant_query_is_answered_from_the_local_docs()
    {
        if (Environment.GetEnvironmentVariable("KGSM_LIVE_OLLAMA") != "1")
            return;

        var model = Environment.GetEnvironmentVariable("KGSM_LIVE_EMBED_MODEL") ?? "embeddinggemma";
        File.WriteAllText(Path.Combine(_dir, "kgsm.md"),
            "# KGSM\n\nKGSM manages game servers through a stateless command-line interface. " +
            "The watchdog owns kgsm.slice and the per-instance cgroups.\n");
        var indexPath = Path.Combine(_dir, "index.krag");

        var embedder = new OllamaEmbeddingClient(
            Options.Create(new RagEmbeddingOptions { EmbeddingModel = model }),
            NullLogger<OllamaEmbeddingClient>.Instance);

        var build = await new IndexBuilder(embedder).BuildAsync(
            new IndexBuilderOptions { Sources = [_dir] }, indexPath);
        build.IsSuccess.Should().BeTrue(because: build.Error);

        var provider = new RagIndexProvider(
            Options.Create(new RagOptions { Enabled = true, IndexPath = indexPath }),
            embedder, NullLogger<RagIndexProvider>.Instance);
        var retrieval = new RagRetrieval(
            embedder, provider, TestInventories.NoGames(), Options.Create(new RagOptions { TopK = 5 }),
            NullLogger<RagRetrieval>.Instance);

        // Log the real top similarity for a clearly-relevant query (a Phase 5 tuning signal).
        var probe = await retrieval.RetrieveAsync("how does kgsm manage game servers?");
        probe.IsSuccess.Should().BeTrue(because: probe.Error);
        var topScore = probe.Value!.Count > 0 ? probe.Value![0].Score : 0;
        Console.WriteLine($"[live] top cosine score for a relevant query = {topScore:F3} (LocalMinScore default = 0.35)");

        // Assert the PATH (threshold 0 → any hit is "strong"); the web is never reached.
        var aggregator = new SearchAggregator(
            retrieval, new NoWeb(), Options.Create(new SearchOptions { LocalMinScore = 0.0 }),
            NullLogger<SearchAggregator>.Instance);

        var grounding = await aggregator.SearchAsync("how does kgsm manage game servers?");

        grounding.Summary.Should().Contain("indexed docs").And.Contain("KGSM manages game servers");
    }

    /// <summary>Stand-in for a disabled web provider — the aggregator must never reach it on a strong local hit.</summary>
    private sealed class NoWeb : IWebSearch
    {
        public Task<Result<IReadOnlyList<WebSearchHit>>> SearchAsync(
            string query, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Failure<IReadOnlyList<WebSearchHit>>("web search is not configured"));
    }
}
