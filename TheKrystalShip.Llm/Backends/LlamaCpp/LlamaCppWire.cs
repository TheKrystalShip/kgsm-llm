using System.Text.Json;
using System.Text.Json.Serialization;

using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Llm.Backends.LlamaCpp;

/// <summary>
/// The body of a <c>POST /v1/chat/completions</c> request, in the order llama-server reads it.
/// </summary>
/// <remarks>
/// <para>
/// Every optional field is nullable and ignored when null, so a knob that is off is absent from the
/// wire rather than sent as a null llama-server would have to interpret. That is what makes
/// "unsaid" and "said to be nothing" different things here, and several of them are: an absent seed
/// leaves the backend's own unseeded sampling alone, and an absent <c>chat_template_kwargs</c> is a
/// template that declares no reasoning variable.
/// </para>
/// <para>
/// The context window is deliberately not a field. llama-server fixes it at launch with <c>-c</c>
/// and ignores a per-request value; it is read from configuration only to stamp token accounting.
/// </para>
/// </remarks>
public sealed record LlamaCppChatRequest
{
    [JsonPropertyName("model")] public string Model { get; init; } = "";
    [JsonPropertyName("stream")] public bool Stream { get; init; }
    [JsonPropertyName("temperature")] public double Temperature { get; init; }
    [JsonPropertyName("messages")] public List<LlamaCppMessage> Messages { get; init; } = [];

    [JsonPropertyName("seed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Seed { get; init; }

    [JsonPropertyName("stream_options")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LlamaCppStreamOptions? StreamOptions { get; init; }

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ToolFunctionPayload>? Tools { get; init; }

    [JsonPropertyName("parallel_tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ParallelToolCalls { get; init; }

    [JsonPropertyName("tool_choice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolChoice { get; init; }

    [JsonPropertyName("chat_template_kwargs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, bool>? ChatTemplateKwargs { get; init; }

    [JsonPropertyName("dry_multiplier")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? DryMultiplier { get; init; }

    [JsonPropertyName("dry_base")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? DryBase { get; init; }

    [JsonPropertyName("dry_allowed_length")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DryAllowedLength { get; init; }

    [JsonPropertyName("dry_penalty_last_n")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DryPenaltyLastN { get; init; }
}

/// <summary>
/// Asks the stream to carry token counts. Without it a streamed turn reports usage as unknown.
/// </summary>
public sealed record LlamaCppStreamOptions
{
    [JsonPropertyName("include_usage")] public bool IncludeUsage { get; init; } = true;
}

/// <summary>
/// One conversation turn. The three shapes the OpenAI format has are one record: a plain turn
/// carries a role and content, an assistant turn requesting tools adds <c>tool_calls</c>, and a
/// result adds the <c>tool_call_id</c> of the call it answers.
/// </summary>
public sealed record LlamaCppMessage
{
    [JsonPropertyName("role")] public string Role { get; init; } = "";
    [JsonPropertyName("content")] public LlamaCppContent Content { get; init; } = "";

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<LlamaCppToolCall>? ToolCalls { get; init; }

    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; init; }
}

public sealed record LlamaCppToolCall
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("type")] public string Type { get; init; } = "function";
    [JsonPropertyName("function")] public LlamaCppToolCallFunction Function { get; init; } = new();
}

/// <summary>
/// A requested call. <see cref="Arguments"/> is JSON nested as text, which is what the OpenAI wire
/// format asks for and what separates it from Ollama's native object.
/// </summary>
public sealed record LlamaCppToolCallFunction
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("arguments")] public string Arguments { get; init; } = "{}";
}

/// <summary>
/// A message's content: text on its own, or text beside the images it is asked about.
/// </summary>
/// <remarks>
/// The OpenAI format spells one field two ways. A turn carrying only words is a plain string; a
/// turn carrying pictures is an array of typed parts, the text first and each image after it as a
/// <c>data:</c> URL. Both are the same <c>content</c> field, so the shape is chosen when the value
/// is written rather than by which property was set, and a message with no images serializes to the
/// string every request built before images existed already sent.
/// </remarks>
[JsonConverter(typeof(LlamaCppContentConverter))]
public sealed record LlamaCppContent(string Text, IReadOnlyList<LlmImage>? Images = null)
{
    /// <summary>Text with no images, which is what every turn but a question about a picture is.</summary>
    public static implicit operator LlamaCppContent(string text) => new(text);
}

/// <summary>
/// Writes <see cref="LlamaCppContent"/> as the string or the array of parts the server expects.
/// </summary>
/// <remarks>
/// Reading is not implemented, and nothing here asks for it: these shapes are what this library
/// sends. A response is walked as a <see cref="JsonDocument"/> rather than bound to a record, so
/// that a server adding fields between versions changes nothing on the inbound path.
/// </remarks>
public sealed class LlamaCppContentConverter : JsonConverter<LlamaCppContent>
{
    public override LlamaCppContent Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        throw new NotSupportedException(
            "A llama.cpp request body is written, never read; responses are parsed as a JsonDocument.");

    public override void Write(Utf8JsonWriter writer, LlamaCppContent value, JsonSerializerOptions options)
    {
        if (value.Images is not { Count: > 0 } images)
        {
            writer.WriteStringValue(value.Text);
            return;
        }

        writer.WriteStartArray();

        writer.WriteStartObject();
        writer.WriteString("type", "text");
        writer.WriteString("text", value.Text);
        writer.WriteEndObject();

        foreach (var image in images)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "image_url");
            writer.WriteStartObject("image_url");
            writer.WriteString("url", image.DataUrl());
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}
