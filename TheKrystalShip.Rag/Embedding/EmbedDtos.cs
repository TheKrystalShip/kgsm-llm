using System.Text.Json.Serialization;

namespace TheKrystalShip.Rag.Embedding;

/// <summary>Request body for Ollama <c>POST /api/embed</c>. <c>input</c> is always an array (single = one element).</summary>
internal sealed class EmbedRequest
{
    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;

    [JsonPropertyName("input")]
    public string[] Input { get; init; } = [];
}

/// <summary>Response body for Ollama <c>POST /api/embed</c>: one vector per input, in order.</summary>
internal sealed class EmbedResponse
{
    [JsonPropertyName("embeddings")]
    public float[][]? Embeddings { get; init; }
}

/// <summary>
/// One entry of the OpenAI <c>POST /v1/embeddings</c> response. <c>index</c> is what maps a vector
/// back to its input — the array's own order is not part of the contract, so it is sorted by this
/// rather than trusted as it arrives.
/// </summary>
internal sealed class OpenAiEmbedDatum
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("embedding")]
    public float[]? Embedding { get; init; }
}

/// <summary>Response body for llama-server's OpenAI-compatible <c>POST /v1/embeddings</c>.</summary>
internal sealed class OpenAiEmbedResponse
{
    [JsonPropertyName("data")]
    public OpenAiEmbedDatum[]? Data { get; init; }
}
