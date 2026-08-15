namespace TheKrystalShip.Rag.Embedding;

/// <summary>
/// The single home for the model → (document, query) task-prefix mapping. Most embedding models
/// are asymmetric: documents and queries are embedded with different instruction prefixes, and
/// Ollama's raw /api/embed does not apply them. Unknown models fall back to no prefix (identity).
/// Prefix strings are a Phase-5 tuning detail; verify against the running Ollama version.
/// </summary>
internal static class EmbeddingPrefixes
{
    private static readonly Dictionary<string, (string Document, string Query)> Table =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // EmbeddingGemma's documented retrieval prompts (Gemma-native, the default — D5).
            ["embeddinggemma"] = ("title: none | text: ", "task: search result | query: "),
            ["nomic-embed-text"] = ("search_document: ", "search_query: "),
            ["mxbai-embed-large"] = ("", "Represent this sentence for searching relevant passages: "),
        };

    public static (string Document, string Query) Resolve(string model)
    {
        var key = Normalize(model);
        return Table.TryGetValue(key, out var prefixes) ? prefixes : (string.Empty, string.Empty);
    }

    /// <summary>Strips an Ollama tag suffix, e.g. <c>nomic-embed-text:latest</c> → <c>nomic-embed-text</c>.</summary>
    private static string Normalize(string model)
    {
        var colon = model.IndexOf(':');
        return (colon >= 0 ? model[..colon] : model).Trim();
    }
}
