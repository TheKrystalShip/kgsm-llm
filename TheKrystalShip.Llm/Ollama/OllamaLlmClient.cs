using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Llm.Ollama;

/// <summary>
/// Raised when the Ollama backend can't be reached or returns an error while streaming. The
/// streaming agent loop maps this to a single terminal <see cref="AgentEvent"/> error, mirroring
/// the buffered <see cref="ILlmClient.ChatAsync"/> path's <c>Result.Failure</c> messages.
/// </summary>
public sealed class OllamaBackendException : Exception
{
    public OllamaBackendException(string message) : base(message) { }
}

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
        bool think = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var json = JsonSerializer.Serialize(BuildBody(messages, tools, stream: false, think));
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

            var toolCalls = OllamaStreamParser.ParseToolCalls(messageElement);
            var usage = OllamaStreamParser.ParseUsage(document.RootElement, _options.NumCtx);

            return Result.Success(new LlmResponse(replyContent, toolCalls, usage));
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

    public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmToolDefinition>? tools = null,
        bool think = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(BuildBody(messages, tools, stream: true, think));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        // ResponseHeadersRead so we get the stream as soon as headers land, not after the whole
        // body — that's the whole point of streaming. Failures throw OllamaBackendException; the
        // agent loop maps that to a terminal error event.
        using var response = await OpenStreamAsync(request, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        // Ollama streams newline-delimited JSON — one frame per line. Parse, yield, stop on done.
        // No try/catch around the yield: a mid-stream read failure throws out of MoveNextAsync,
        // which the agent loop catches; disposing this iterator early aborts the HTTP read.
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            var chunk = OllamaStreamParser.ParseFrame(line, _options.NumCtx);
            if (chunk is null)
                continue;
            yield return chunk;
            if (chunk.Done)
                yield break;
        }
    }

    private async Task<HttpResponseMessage> OpenStreamAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError("Ollama stream timed out after {Timeout}s", _options.TimeoutSeconds);
            throw new OllamaBackendException("The LLM took too long to respond.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error opening Ollama stream");
            throw new OllamaBackendException("Could not reach the LLM backend.");
        }

        if (response.IsSuccessStatusCode)
            return response;

        var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogError("Ollama returned {StatusCode}: {Body}", (int)response.StatusCode, errorBody);
        response.Dispose();
        throw new OllamaBackendException($"LLM backend returned status {(int)response.StatusCode}.");
    }

    private Dictionary<string, object?> BuildBody(
        IReadOnlyList<LlmMessage> messages, IReadOnlyList<LlmToolDefinition>? tools, bool stream, bool think)
    {
        var options = new Dictionary<string, object?>
        {
            ["num_ctx"] = _options.NumCtx,
            ["temperature"] = _options.Temperature
        };
        // Only sent when explicitly configured (the eval harness's reproducible-run mode); an
        // absent seed leaves Ollama's default unseeded sampling untouched.
        if (_options.Seed is int seed)
            options["seed"] = seed;

        var body = new Dictionary<string, object?>
        {
            ["model"] = _options.Model,
            ["stream"] = stream,
            ["think"] = think,
            ["messages"] = messages.Select(BuildMessagePayload).ToArray(),
            ["options"] = options
        };

        if (tools is { Count: > 0 })
            body["tools"] = tools.Select(BuildToolPayload).ToArray();

        return body;
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

    internal static object BuildToolPayload(LlmToolDefinition tool) => new
    {
        type = "function",
        function = new
        {
            name = tool.Name,
            description = tool.Description,
            parameters = new
            {
                type = "object",
                properties = tool.Parameters.ToDictionary(p => p.Name, BuildParameterSchema),
                required = tool.Parameters.Where(p => p.Required).Select(p => p.Name).ToArray()
            }
        }
    };

    /// <summary>
    /// Builds one parameter's JSON-schema object. A parameter with
    /// <see cref="LlmToolParameter.AllowedValues"/> gains an <c>enum</c> constraint,
    /// which steers the model to a valid value (the small-model reliability lever).
    /// </summary>
    private static object BuildParameterSchema(LlmToolParameter p)
    {
        var schema = new Dictionary<string, object>
        {
            ["type"] = p.Type,
            ["description"] = p.Description,
        };
        if (p.AllowedValues is { Count: > 0 })
            schema["enum"] = p.AllowedValues;
        return schema;
    }
}
