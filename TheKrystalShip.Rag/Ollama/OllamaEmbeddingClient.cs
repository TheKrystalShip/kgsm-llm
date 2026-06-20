using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.Rag.Models;

namespace TheKrystalShip.Rag.Ollama;

/// <summary>
/// <see cref="IEmbeddingClient"/> backed by a local Ollama server (<c>POST /api/embed</c>).
/// Mirrors <c>OllamaLlmClient</c>'s HTTP/error idiom (Result on failure, terse user-facing
/// messages) but is reflection-free: serialization goes through the source-generated
/// <see cref="RagJsonContext"/> so the core stays Native-AOT clean.
/// </summary>
public sealed class OllamaEmbeddingClient : IEmbeddingClient
{
    private readonly HttpClient _httpClient;
    private readonly RagEmbeddingOptions _options;
    private readonly ILogger<OllamaEmbeddingClient> _logger;

    public OllamaEmbeddingClient(
        IOptions<RagEmbeddingOptions> options,
        ILogger<OllamaEmbeddingClient> logger)
        : this(BuildHttpClient(options.Value), options.Value, logger)
    {
    }

    // Test seam: inject a HttpClient (with a stub handler) without going through the real network.
    internal OllamaEmbeddingClient(
        HttpClient httpClient,
        RagEmbeddingOptions options,
        ILogger<OllamaEmbeddingClient> logger)
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

        var request = new EmbedRequest { Model = _options.EmbeddingModel, Input = prefixed };

        try
        {
            var json = JsonSerializer.Serialize(request, RagJsonContext.Default.EmbedRequest);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await _httpClient.PostAsync("/api/embed", content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError(
                    "Ollama embed returned {StatusCode}: {Body}", (int)response.StatusCode, body);
                return Result.Failure<IReadOnlyList<float[]>>(
                    $"Embedding backend returned status {(int)response.StatusCode}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var parsed = await JsonSerializer.DeserializeAsync(
                stream, RagJsonContext.Default.EmbedResponse, cancellationToken);

            if (parsed?.Embeddings is null || parsed.Embeddings.Length != inputs.Count)
            {
                _logger.LogError(
                    "Ollama embed response shape unexpected: {Got} vectors for {Expected} inputs.",
                    parsed?.Embeddings?.Length ?? 0, inputs.Count);
                return Result.Failure<IReadOnlyList<float[]>>(
                    "Embedding backend returned an unexpected response shape.");
            }

            foreach (var vector in parsed.Embeddings)
            {
                if (vector is null || vector.Length == 0)
                    return Result.Failure<IReadOnlyList<float[]>>(
                        "Embedding backend returned an empty vector.");
            }

            return Result<IReadOnlyList<float[]>>.Success(parsed.Embeddings);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError("Ollama embed timed out after {Timeout}s", _options.TimeoutSeconds);
            return Result.Failure<IReadOnlyList<float[]>>("The embedding backend took too long to respond.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling Ollama embed endpoint");
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
