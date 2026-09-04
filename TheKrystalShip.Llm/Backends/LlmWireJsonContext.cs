using System.Text.Json.Serialization;

using TheKrystalShip.Llm.Backends.LlamaCpp;
using TheKrystalShip.Llm.Backends.Ollama;

namespace TheKrystalShip.Llm.Backends;

/// <summary>
/// Source-generated serialization for everything a backend puts on the wire.
/// </summary>
/// <remarks>
/// <para>
/// The two request bodies and the tool-argument dictionary nested inside a llama.cpp tool call are
/// the whole of what this library serializes on the outbound path. Declaring them here is what lets
/// a trimmed or ahead-of-time-compiled host talk to a model: a reflective serializer needs types it
/// can only discover at runtime, which such a host has thrown away.
/// </para>
/// <para>
/// Nothing configures the options. A wire body is compared byte for byte against what a server was
/// measured to accept, and an encoder or a naming policy set here would change every string in it.
/// The shapes carry their own <c>[JsonPropertyName]</c> and their own null handling, so a body reads
/// the same however it is serialized.
/// </para>
/// <para>
/// Inbound responses are read with <c>JsonDocument</c> rather than bound to a shape, so they need no
/// entry here: a backend adds fields between versions, and a reader that walks the document takes
/// what it knows and ignores the rest.
/// </para>
/// </remarks>
[JsonSerializable(typeof(LlamaCppChatRequest))]
[JsonSerializable(typeof(OllamaChatRequest))]
[JsonSerializable(typeof(Dictionary<string, string?>))]
public sealed partial class LlmWireJsonContext : JsonSerializerContext;
