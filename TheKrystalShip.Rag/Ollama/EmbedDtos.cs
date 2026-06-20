using System.Text.Json.Serialization;

namespace TheKrystalShip.Rag.Ollama;

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
