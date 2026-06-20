using System.Text.Json.Serialization;

namespace TheKrystalShip.Rag.Ollama;

/// <summary>
/// Source-generated JSON context for the embedding wire DTOs. This is what keeps the RAG core
/// reflection-free and Native-AOT clean — all (de)serialization goes through
/// <see cref="Default"/>, never the reflection-based <c>JsonSerializer</c> overloads.
/// </summary>
[JsonSerializable(typeof(EmbedRequest))]
[JsonSerializable(typeof(EmbedResponse))]
internal sealed partial class RagJsonContext : JsonSerializerContext;
