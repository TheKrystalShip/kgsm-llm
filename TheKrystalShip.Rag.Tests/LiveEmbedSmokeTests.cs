using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using TheKrystalShip.Rag.Embedding;

namespace TheKrystalShip.Rag.Tests;

/// <summary>
/// Gated live smoke against a real Ollama (mirrors the repo's KGSM_LIVE_OLLAMA convention).
/// Run with <c>KGSM_LIVE_OLLAMA=1</c> and the embed model pulled
/// (<c>ollama pull embeddinggemma</c>); otherwise it no-ops. Override the model via
/// <c>KGSM_LIVE_EMBED_MODEL</c>.
/// </summary>
public class LiveEmbedSmokeTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("KGSM_LIVE_OLLAMA") == "1";

    private static OllamaEmbeddingClient LiveClient()
    {
        var options = new RagEmbeddingOptions
        {
            EmbeddingModel = Environment.GetEnvironmentVariable("KGSM_LIVE_EMBED_MODEL") ?? "embeddinggemma",
        };
        return new OllamaEmbeddingClient(Options.Create(options), NullLogger<OllamaEmbeddingClient>.Instance);
    }

    [Fact]
    public async Task Document_and_query_embeddings_come_back_with_a_consistent_dimension()
    {
        if (!Enabled)
            return; // no-op unless explicitly enabled

        var client = LiveClient();

        var documents = await client.EmbedDocumentsAsync(["KGSM manages game servers via a stateless CLI."]);
        var query = await client.EmbedQueryAsync("How does KGSM manage servers?");

        documents.IsSuccess.Should().BeTrue(because: documents.Error);
        query.IsSuccess.Should().BeTrue(because: query.Error);

        var documentVectors = documents.Value!;
        documentVectors.Should().ContainSingle();
        documentVectors[0].Length.Should().BeGreaterThan(0);
        // Same model → same vector space → identical dimension for docs and queries.
        query.Value!.Length.Should().Be(documentVectors[0].Length);
    }
}
