using System.Text.Json.Serialization;

using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Llm.Backends.Ollama;

/// <summary>
/// The body of a <c>POST /api/chat</c> request.
/// </summary>
/// <remarks>
/// Ollama takes the context window per request, in <c>options</c>, which is the one place its native
/// shape asks for something llama.cpp fixes at launch. A tool call's arguments travel as an object
/// here rather than as nested text, a result names the tool it came from rather than a call id, and
/// images are a field of the message rather than parts of its content.
/// </remarks>
public sealed record OllamaChatRequest
{
    [JsonPropertyName("model")] public string Model { get; init; } = "";
    [JsonPropertyName("stream")] public bool Stream { get; init; }
    [JsonPropertyName("think")] public bool Think { get; init; }
    [JsonPropertyName("messages")] public List<OllamaMessage> Messages { get; init; } = [];
    [JsonPropertyName("options")] public OllamaOptions Options { get; init; } = new();

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ToolFunctionPayload>? Tools { get; init; }
}

public sealed record OllamaOptions
{
    [JsonPropertyName("num_ctx")] public int NumCtx { get; init; }
    [JsonPropertyName("temperature")] public double Temperature { get; init; }

    [JsonPropertyName("seed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Seed { get; init; }
}

public sealed record OllamaMessage
{
    [JsonPropertyName("role")] public string Role { get; init; } = "";
    [JsonPropertyName("content")] public string Content { get; init; } = "";

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<OllamaToolCall>? ToolCalls { get; init; }

    [JsonPropertyName("tool_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolName { get; init; }

    /// <summary>The images this turn shows the model, each base64-encoded with no data-URL prefix.</summary>
    [JsonPropertyName("images")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Images { get; init; }
}

public sealed record OllamaToolCall
{
    [JsonPropertyName("function")] public OllamaToolCallFunction Function { get; init; } = new();
}

public sealed record OllamaToolCallFunction
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("arguments")] public Dictionary<string, string?> Arguments { get; init; } = [];
}

/// <summary>Builds the <c>/api/chat</c> request body.</summary>
public static class OllamaRequestBuilder
{
    public static OllamaChatRequest Build(
        LlmBackendOptions options,
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmToolDefinition>? tools,
        bool stream,
        bool think) => new()
        {
            Model = options.Model,
            Stream = stream,
            Think = think,
            Messages = messages.Select(BuildMessage).ToList(),
            Options = new OllamaOptions
            {
                NumCtx = options.ContextWindow,
                Temperature = options.Temperature,

                // Only sent when explicitly configured (the eval harness's reproducible-run mode); an
                // absent seed leaves Ollama's default unseeded sampling untouched.
                Seed = options.Seed,
            },
            Tools = tools is { Count: > 0 } ? tools.Select(ToolSchema.BuildFunction).ToList() : null,
        };

    private static OllamaMessage BuildMessage(LlmMessage message) => new()
    {
        Role = message.Role.ToString().ToLowerInvariant(),
        Content = message.Content ?? string.Empty,
        ToolCalls = message.ToolCalls is { Count: > 0 } calls
            ? calls.Select(call => new OllamaToolCall
            {
                Function = new OllamaToolCallFunction
                {
                    Name = call.Name.Name,
                    Arguments = new Dictionary<string, string?>(call.Arguments, StringComparer.Ordinal),
                },
            }).ToList()
            : null,
        ToolName = message.ToolName?.Name,
        Images = message.Images is { Count: > 0 } images
            ? images.Select(image => image.Base64()).ToList()
            : null,
    };
}
