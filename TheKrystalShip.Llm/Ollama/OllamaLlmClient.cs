using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Llm.Ollama;

/// <summary>
/// <see cref="ILlmClient"/> implementation backed by a local Ollama server
/// (POST /api/chat, non-streaming, with tool-calling support).
/// </summary>
public class OllamaLlmClient : ILlmClient
{
    private readonly HttpClient _httpClient;
    private readonly OllamaOptions _options;
    private readonly ILogger<OllamaLlmClient> _logger;

    public OllamaLlmClient(
        IOptions<OllamaOptions> options,
        ILogger<OllamaLlmClient> logger)
    {
        _options = options.Value;
        _logger = logger;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_options.Endpoint),
            Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds)
        };
    }

    public async Task<Result<LlmResponse>> ChatAsync(
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmToolDefinition>? tools = null,
        CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = _options.Model,
            ["stream"] = false,
            ["messages"] = messages.Select(BuildMessagePayload).ToArray(),
            ["options"] = new Dictionary<string, object?>
            {
                ["num_ctx"] = _options.NumCtx,
                ["temperature"] = _options.Temperature
            }
        };

        if (tools is { Count: > 0 })
            body["tools"] = tools.Select(BuildToolPayload).ToArray();

        try
        {
            var json = JsonSerializer.Serialize(body);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await _httpClient.PostAsync("/api/chat", content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError(
                    "Ollama returned {StatusCode}: {Body}", (int)response.StatusCode, errorBody);
                return Result.Failure<LlmResponse>(
                    $"LLM backend returned status {(int)response.StatusCode}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (!document.RootElement.TryGetProperty("message", out var messageElement))
            {
                _logger.LogError("Ollama response missing 'message'");
                return Result.Failure<LlmResponse>("LLM backend returned an unexpected response shape.");
            }

            var replyContent = messageElement.TryGetProperty("content", out var contentElement)
                ? contentElement.GetString()?.Trim()
                : null;

            var toolCalls = ParseToolCalls(messageElement);

            return Result.Success(new LlmResponse(replyContent, toolCalls));
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError("Ollama request timed out after {Timeout}s", _options.TimeoutSeconds);
            return Result.Failure<LlmResponse>("The LLM took too long to respond.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling Ollama chat endpoint");
            return Result.Failure<LlmResponse>("Could not reach the LLM backend.");
        }
    }

    private static object BuildMessagePayload(LlmMessage message)
    {
        var payload = new Dictionary<string, object?>
        {
            ["role"] = message.Role.ToString().ToLowerInvariant(),
            ["content"] = message.Content ?? string.Empty
        };

        if (message.ToolCalls is { Count: > 0 })
        {
            payload["tool_calls"] = message.ToolCalls.Select(tc => new
            {
                function = new { name = tc.Name, arguments = tc.Arguments }
            }).ToArray();
        }

        if (message.ToolName is not null)
            payload["tool_name"] = message.ToolName;

        return payload;
    }

    private static object BuildToolPayload(LlmToolDefinition tool) => new
    {
        type = "function",
        function = new
        {
            name = tool.Name,
            description = tool.Description,
            parameters = new
            {
                type = "object",
                properties = tool.Parameters.ToDictionary(
                    p => p.Name,
                    p => (object)new { type = p.Type, description = p.Description }),
                required = tool.Parameters.Where(p => p.Required).Select(p => p.Name).ToArray()
            }
        }
    };

    private List<LlmToolCall> ParseToolCalls(JsonElement messageElement)
    {
        var toolCalls = new List<LlmToolCall>();

        if (!messageElement.TryGetProperty("tool_calls", out var toolCallsElement) ||
            toolCallsElement.ValueKind != JsonValueKind.Array)
            return toolCalls;

        foreach (var toolCall in toolCallsElement.EnumerateArray())
        {
            if (!toolCall.TryGetProperty("function", out var function) ||
                !function.TryGetProperty("name", out var nameElement))
                continue;

            var name = nameElement.GetString();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var arguments = new Dictionary<string, string?>();
            if (function.TryGetProperty("arguments", out var argsElement))
                ExtractArguments(argsElement, arguments);

            toolCalls.Add(new LlmToolCall(name, arguments));
        }

        return toolCalls;
    }

    /// <summary>
    /// Arguments usually arrive as a JSON object, but some models emit a JSON
    /// string that itself contains an object. Handle both, and coerce each value
    /// to a string regardless of its JSON kind.
    /// </summary>
    private void ExtractArguments(JsonElement argsElement, Dictionary<string, string?> into)
    {
        if (argsElement.ValueKind == JsonValueKind.String)
        {
            var raw = argsElement.GetString();
            if (string.IsNullOrWhiteSpace(raw))
                return;
            try
            {
                using var parsed = JsonDocument.Parse(raw);
                if (parsed.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var prop in parsed.RootElement.EnumerateObject())
                        into[prop.Name] = JsonValueToString(prop.Value);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Could not parse stringified tool arguments: {Raw}", raw);
            }
            return;
        }

        if (argsElement.ValueKind == JsonValueKind.Object)
            foreach (var prop in argsElement.EnumerateObject())
                into[prop.Name] = JsonValueToString(prop.Value);
    }

    private static string? JsonValueToString(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Null => null,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => element.GetRawText(),
        _ => element.GetRawText()
    };
}
