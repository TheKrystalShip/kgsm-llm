using System.Text.Json;

using FluentAssertions;

using TheKrystalShip.Rag.Ollama;

namespace TheKrystalShip.Rag.Tests;

/// <summary>
/// Proves the source-generated <see cref="RagJsonContext"/> is wired correctly — the AOT
/// analyzer alone can't catch a missing [JsonSerializable], a wrong [JsonPropertyName], or
/// mishandled <c>float[][]</c>. Analyzer-clean + this round-trip = actually AOT-safe.
/// </summary>
public class EmbedJsonContextTests
{
    [Fact]
    public void EmbedRequest_serializes_with_the_expected_wire_shape()
    {
        var request = new EmbedRequest { Model = "nomic-embed-text", Input = ["a", "b"] };

        var json = JsonSerializer.Serialize(request, RagJsonContext.Default.EmbedRequest);

        json.Should().Contain("\"model\":\"nomic-embed-text\"");
        json.Should().Contain("\"input\":[\"a\",\"b\"]");
    }

    [Fact]
    public void EmbedResponse_deserializes_the_embeddings_matrix()
    {
        const string json = "{\"embeddings\":[[0.1,0.2],[0.3,0.4]]}";

        var response = JsonSerializer.Deserialize(json, RagJsonContext.Default.EmbedResponse);

        response.Should().NotBeNull();
        response!.Embeddings.Should().HaveCount(2);
        response.Embeddings![0].Should().HaveCount(2);
        response.Embeddings[0][0].Should().BeApproximately(0.1f, 1e-6f);
        response.Embeddings[1][1].Should().BeApproximately(0.4f, 1e-6f);
    }
}
