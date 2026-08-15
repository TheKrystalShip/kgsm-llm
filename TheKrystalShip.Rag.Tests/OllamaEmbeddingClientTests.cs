using System.Net;
using System.Text;
using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using TheKrystalShip.Rag.Embedding;

namespace TheKrystalShip.Rag.Tests;

public class OllamaEmbeddingClientTests
{
    [Fact]
    public async Task EmbedQueryAsync_parses_the_vector_and_applies_the_query_prefix()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"embeddings\":[[1.0,2.0,3.0]]}");
        var client = ClientWith(handler, new RagEmbeddingOptions { EmbeddingModel = "nomic-embed-text" });

        var result = await client.EmbedQueryAsync("hello");

        result.IsSuccess.Should().BeTrue();
        result.Value!.Should().Equal(1f, 2f, 3f);
        // The model's asymmetric query prefix is applied to the input.
        InputsOf(handler).Should().ContainSingle().Which.Should().Be("search_query: hello");
    }

    [Fact]
    public async Task EmbedDocumentsAsync_applies_the_document_prefix_to_each_input()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"embeddings\":[[1.0],[2.0]]}");
        var client = ClientWith(handler, new RagEmbeddingOptions { EmbeddingModel = "nomic-embed-text" });

        var result = await client.EmbedDocumentsAsync(["d1", "d2"]);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        InputsOf(handler).Should().Equal("search_document: d1", "search_document: d2");
    }

    [Fact]
    public async Task The_default_embeddinggemma_applies_its_own_task_prompts()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"embeddings\":[[1.0]]}");
        var client = ClientWith(handler); // default model = embeddinggemma

        var result = await client.EmbedQueryAsync("hello");

        result.IsSuccess.Should().BeTrue();
        InputsOf(handler).Should().ContainSingle().Which.Should().Be("task: search result | query: hello");
    }

    [Fact]
    public async Task A_vector_count_that_mismatches_the_inputs_is_a_failure()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"embeddings\":[[1.0]]}"); // 1 row for 2 inputs
        var client = ClientWith(handler);

        var result = await client.EmbedDocumentsAsync(["d1", "d2"]);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("unexpected response shape");
    }

    [Fact]
    public async Task A_non_success_status_is_a_failure()
    {
        var handler = new StubHandler(HttpStatusCode.InternalServerError, "boom");
        var client = ClientWith(handler);

        var result = await client.EmbedQueryAsync("hello");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("status 500");
    }

    [Fact]
    public async Task Empty_documents_short_circuits_without_calling_the_backend()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");
        var client = ClientWith(handler);

        var result = await client.EmbedDocumentsAsync([]);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
        handler.CallCount.Should().Be(0);
    }

    private static OllamaEmbeddingClient ClientWith(StubHandler handler, RagEmbeddingOptions? options = null)
    {
        options ??= new RagEmbeddingOptions();
        var http = new HttpClient(handler) { BaseAddress = new Uri(options.Endpoint) };
        return new OllamaEmbeddingClient(http, options, NullLogger<OllamaEmbeddingClient>.Instance);
    }

    private static string?[] InputsOf(StubHandler handler)
    {
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        return doc.RootElement.GetProperty("input").EnumerateArray().Select(e => e.GetString()).ToArray();
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }
        public int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
