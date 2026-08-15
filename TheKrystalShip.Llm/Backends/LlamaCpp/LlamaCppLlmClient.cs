using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Llm.Backends.LlamaCpp;

/// <summary>
/// Raised when llama-server can't be reached or returns an error while streaming. The streaming
/// agent loop maps this to a single terminal <see cref="Models.AgentEvent"/> error, mirroring the
/// buffered <see cref="ILlmClient.ChatAsync"/> path's <c>Result.Failure</c> messages.
/// </summary>
public sealed class LlamaCppBackendException : Exception
{
    public LlamaCppBackendException(string message) : base(message) { }
}

/// <summary>
/// <see cref="ILlmClient"/> implementation backed by llama.cpp's <c>llama-server</c> over its
/// OpenAI-compatible <c>POST /v1/chat/completions</c> endpoint, buffered and streaming.
/// <para>
/// The server must be started with <c>--jinja</c> and a tools-capable chat template, or it accepts
/// the <c>tools</c> array and never emits a tool call — the assistant would answer normally and
/// simply never act. <c>GET /props</c> reports the template it loaded.
/// </para>
/// </summary>
public class LlamaCppLlmClient : ILlmClient
{
    private const string ChatPath = "/v1/chat/completions";

    private readonly HttpClient _httpClient;
    private readonly LlmBackendOptions _options;
    private readonly LlamaCppOptions _llamaCpp;
    private readonly ILogger<LlamaCppLlmClient> _logger;

    public LlamaCppLlmClient(
        IOptions<LlmBackendOptions> options,
        IOptions<LlamaCppOptions> llamaCppOptions,
        ILogger<LlamaCppLlmClient> logger)
    {
        _options = options.Value;
        _llamaCpp = llamaCppOptions.Value;
        _logger = logger;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_options.Endpoint),
            Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds)
        };

        if (!string.IsNullOrWhiteSpace(_llamaCpp.ApiKey))
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _llamaCpp.ApiKey);
    }

    public async Task<Result<LlmResponse>> ChatAsync(
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmToolDefinition>? tools = null,
        bool think = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var json = JsonSerializer.Serialize(
                LlamaCppRequestBuilder.Build(_options, _llamaCpp, messages, tools, stream: false, think));
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await _httpClient.PostAsync(ChatPath, content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError(
                    "llama-server returned {StatusCode}: {Body}", (int)response.StatusCode, errorBody);
                return Result.Failure<LlmResponse>(
                    $"LLM backend returned status {(int)response.StatusCode}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (LlamaCppStreamParser.ParseBuffered(document.RootElement, _options.ContextWindow) is not { } parsed)
            {
                _logger.LogError("llama-server response missing 'choices[0].message'");
                return Result.Failure<LlmResponse>("LLM backend returned an unexpected response shape.");
            }

            return Result.Success(parsed);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError("llama-server request timed out after {Timeout}s", _options.TimeoutSeconds);
            return Result.Failure<LlmResponse>("The LLM took too long to respond.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling llama-server chat endpoint");
            return Result.Failure<LlmResponse>("Could not reach the LLM backend.");
        }
    }

    public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmToolDefinition>? tools = null,
        bool think = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(
            LlamaCppRequestBuilder.Build(_options, _llamaCpp, messages, tools, stream: true, think));

        using var request = new HttpRequestMessage(HttpMethod.Post, ChatPath)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        // ResponseHeadersRead so we get the stream as soon as headers land, not after the whole
        // body — that's the whole point of streaming. Failures throw LlamaCppBackendException; the
        // agent loop maps that to a terminal error event.
        using var response = await OpenStreamAsync(request, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        var parser = new LlamaCppStreamParser(_options.ContextWindow);

        // Server-Sent Events: one `data:` line per frame, blank lines between them. No try/catch
        // around the yield: a mid-stream read failure throws out of MoveNextAsync, which the agent
        // loop catches; disposing this iterator early aborts the HTTP read.
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            var chunk = parser.ParseFrame(line);
            if (chunk is null)
                continue;
            yield return chunk;
            if (chunk.Done)
                yield break;
        }

        // The stream ended without a [DONE] sentinel. Close it out so the consumer still sees a
        // terminal frame rather than an enumeration that simply stops.
        if (parser.Finish() is { } terminal)
            yield return terminal;
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
            _logger.LogError("llama-server stream timed out after {Timeout}s", _options.TimeoutSeconds);
            throw new LlamaCppBackendException("The LLM took too long to respond.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error opening llama-server stream");
            throw new LlamaCppBackendException("Could not reach the LLM backend.");
        }

        if (response.IsSuccessStatusCode)
            return response;

        var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogError("llama-server returned {StatusCode}: {Body}", (int)response.StatusCode, errorBody);
        response.Dispose();
        throw new LlamaCppBackendException($"LLM backend returned status {(int)response.StatusCode}.");
    }
}
