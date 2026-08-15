using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.Rag.Models;

namespace TheKrystalShip.Rag.Embedding;

/// <summary>
/// <see cref="IEmbeddingClient"/> backed by llama.cpp's <c>llama-server</c> over its
/// OpenAI-compatible <c>POST /v1/embeddings</c> endpoint. The server has to have been started with
/// <c>--embedding</c>; without it the route answers an error and no vector is produced.
/// <para>
/// Reflection-free like its Ollama counterpart — serialization goes through the source-generated
/// <see cref="RagJsonContext"/> so the core stays Native-AOT clean.
/// </para>
/// </summary>
public sealed class LlamaCppEmbeddingClient : IEmbeddingClient
{
    private readonly HttpClient _httpClient;
    private readonly RagEmbeddingOptions _options;
    private readonly ILogger<LlamaCppEmbeddingClient> _logger;

    public LlamaCppEmbeddingClient(
        IOptions<RagEmbeddingOptions> options,
        ILogger<LlamaCppEmbeddingClient> logger)
        : this(BuildHttpClient(options.Value), options.Value, logger)
    {
    }

    // Test seam: inject a HttpClient (with a stub handler) without going through the real network.
    internal LlamaCppEmbeddingClient(
        HttpClient httpClient,
        RagEmbeddingOptions options,
        ILogger<LlamaCppEmbeddingClient> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
    }

    public string ModelName => _options.EmbeddingModel;

    public Task<Result<IReadOnlyList<float[]>>> EmbedDocumentsAsync(
        IReadOnlyList<string> documents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        return EmbedAsync(documents, ResolvePrefixes().Document, cancellationToken);
    }

    public async Task<Result<float[]>> EmbedQueryAsync(string query, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var result = await EmbedAsync([query], ResolvePrefixes().Query, cancellationToken);
        return result.IsSuccess
            ? Result.Success(result.Value![0])
            : Result.Failure<float[]>(result.Error!);
    }

    private async Task<Result<IReadOnlyList<float[]>>> EmbedAsync(
        IReadOnlyList<string> inputs, string prefix, CancellationToken cancellationToken)
    {
        if (inputs.Count == 0)
            return Result<IReadOnlyList<float[]>>.Success(Array.Empty<float[]>());

        var prefixed = new string[inputs.Count];
        for (var i = 0; i < inputs.Count; i++)
            prefixed[i] = prefix + inputs[i];

        // Same request shape as Ollama's /api/embed — {model, input[]} — so the one DTO serves both.
        var request = new EmbedRequest { Model = _options.EmbeddingModel, Input = prefixed };

        try
        {
            var json = JsonSerializer.Serialize(request, RagJsonContext.Default.EmbedRequest);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await _httpClient.PostAsync("/v1/embeddings", content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError(
                    "llama-server embed returned {StatusCode}: {Body}", (int)response.StatusCode, body);
                return Result.Failure<IReadOnlyList<float[]>>(
                    $"Embedding backend returned status {(int)response.StatusCode}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var parsed = await JsonSerializer.DeserializeAsync(
                stream, RagJsonContext.Default.OpenAiEmbedResponse, cancellationToken);

            if (parsed?.Data is null || parsed.Data.Length != inputs.Count)
            {
                _logger.LogError(
                    "llama-server embed response shape unexpected: {Got} vectors for {Expected} inputs.",
                    parsed?.Data?.Length ?? 0, inputs.Count);
                return Result.Failure<IReadOnlyList<float[]>>(
                    "Embedding backend returned an unexpected response shape.");
            }

            // Ordered by the entry's own index, never by arrival: a vector paired with the wrong
            // chunk is an index that retrieves confidently and wrongly, with nothing to show for it.
            var ordered = new float[inputs.Count][];
            foreach (var datum in parsed.Data)
            {
                if (datum.Index < 0 || datum.Index >= inputs.Count || ordered[datum.Index] is not null)
                {
                    _logger.LogError(
                        "llama-server embed returned an out-of-range or duplicated index {Index} for {Expected} inputs.",
                        datum.Index, inputs.Count);
                    return Result.Failure<IReadOnlyList<float[]>>(
                        "Embedding backend returned an unexpected response shape.");
                }

                if (datum.Embedding is null || datum.Embedding.Length == 0)
                    return Result.Failure<IReadOnlyList<float[]>>(
                        "Embedding backend returned an empty vector.");

                ordered[datum.Index] = datum.Embedding;
            }

            return Result<IReadOnlyList<float[]>>.Success(ordered);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError("llama-server embed timed out after {Timeout}s", _options.TimeoutSeconds);
            return Result.Failure<IReadOnlyList<float[]>>("The embedding backend took too long to respond.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling llama-server embed endpoint");
            return Result.Failure<IReadOnlyList<float[]>>("Could not reach the embedding backend.");
        }
    }

    private (string Document, string Query) ResolvePrefixes()
    {
        var (document, query) = EmbeddingPrefixes.Resolve(_options.EmbeddingModel);
        return (_options.DocumentPrefix ?? document, _options.QueryPrefix ?? query);
    }

    private static HttpClient BuildHttpClient(RagEmbeddingOptions options) => new()
    {
        BaseAddress = new Uri(options.Endpoint),
        Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
    };
}
