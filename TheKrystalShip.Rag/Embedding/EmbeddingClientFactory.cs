using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TheKrystalShip.Rag.Embedding;

/// <summary>
/// Builds the <see cref="IEmbeddingClient"/> for the configured
/// <see cref="RagEmbeddingOptions.Provider"/>. The indexer daemon and its watcher construct their
/// embedder directly rather than through DI — they are AOT console hosts with no container — so the
/// choice lives here instead of at each of those call sites, where a new provider would otherwise
/// have to be remembered three times.
/// </summary>
public static class EmbeddingClientFactory
{
    public static IEmbeddingClient Create(RagEmbeddingOptions options, ILoggerFactory loggerFactory) =>
        options.Provider switch
        {
            EmbeddingProvider.LlamaCpp => new LlamaCppEmbeddingClient(
                Options.Create(options), loggerFactory.CreateLogger<LlamaCppEmbeddingClient>()),
            _ => new OllamaEmbeddingClient(
                Options.Create(options), loggerFactory.CreateLogger<OllamaEmbeddingClient>())
        };
}
