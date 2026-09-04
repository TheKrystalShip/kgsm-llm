using System.Text.Json;

using FluentAssertions;

using TheKrystalShip.Llm.Backends;
using TheKrystalShip.Llm.Backends.LlamaCpp;
using TheKrystalShip.Llm.Backends.Ollama;
using TheKrystalShip.Llm.Models;

using Xunit;

namespace TheKrystalShip.Llm.Tests;

/// <summary>
/// The bytes each backend is sent, held against a recorded body its server accepts.
/// </summary>
/// <remarks>
/// A request body is a contract with a process no test here can call, so what is worth pinning is
/// the text: field names, field order, which optional fields are absent rather than null, and the
/// escaping inside a tool call's nested argument string. Serialization goes through
/// <see cref="LlmWireJsonContext"/>, which is the path a trimmed or ahead-of-time host takes and so
/// the only one worth measuring. A change to any of it fails here rather than reaching a model that
/// quietly stops calling tools.
/// </remarks>
public class LlmWireFormatTests
{
    private static readonly LlmBackendOptions Backend = new()
    {
        Model = "gemma4:12b",
        ContextWindow = 32768,
        Temperature = 0.3,
        Seed = 42,
    };

    private static readonly LlmToolDefinition Tool = LlmToolDefinition.Create(
        new Tool("server_command"), "Acts on a server",
        new LlmToolParameter("instance", "the server"),
        new LlmToolParameter("verb", "What to do", Required: false, AllowedValues: ["start", "stop"]));

    /// <summary>A whole round: a system turn, a question, a tool call, its result and the answer.</summary>
    private static List<LlmMessage> Conversation() =>
    [
        LlmMessage.System("you are a thing"),
        LlmMessage.User("status?"),
        LlmMessage.AssistantToolCalls([
            new LlmToolCall(new Tool("server_command"),
                new Dictionary<string, string?> { ["instance"] = "terraria", ["verb"] = "start" })
        ]),
        LlmMessage.Tool(new Tool("server_command"), "running"),
        LlmMessage.Assistant("it is running"),
    ];

    private static string Recorded(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "wire", name)).Trim();

    [Fact]
    public void LlamaCpp_SendsTheRecordedBody()
    {
        var json = JsonSerializer.Serialize(
            LlamaCppRequestBuilder.Build(Backend, new LlamaCppOptions(), Conversation(), [Tool],
                stream: true, think: false),
            LlmWireJsonContext.Default.LlamaCppChatRequest);

        json.Should().Be(Recorded("llamacpp-chat-request.json"));
    }

    [Fact]
    public void Ollama_SendsTheRecordedBody()
    {
        var json = JsonSerializer.Serialize(
            OllamaRequestBuilder.Build(Backend, Conversation(), [Tool], stream: false, think: true),
            LlmWireJsonContext.Default.OllamaChatRequest);

        json.Should().Be(Recorded("ollama-chat-request.json"));
    }

    /// <summary>
    /// A knob that is off is absent from the body, never sent as a null the server would have to
    /// interpret. Unsaid and said-to-be-nothing are different requests: an absent seed leaves the
    /// backend's own sampling alone, and an absent thinking variable is a template that declares none.
    /// </summary>
    [Fact]
    public void WhatIsTurnedOffIsAbsentRatherThanNull()
    {
        var json = JsonSerializer.Serialize(
            LlamaCppRequestBuilder.Build(
                new LlmBackendOptions { Model = "m" },
                new LlamaCppOptions { DryMultiplier = 0, ThinkingTemplateKwarg = "" },
                [LlmMessage.User("hi")], null, stream: false, think: false),
            LlmWireJsonContext.Default.LlamaCppChatRequest);

        json.Should().NotContain("null");
        json.Should().Be(Recorded("llamacpp-everything-off.json"));
    }

    /// <summary>
    /// The generated context and reflection have to produce the same body, because the request-shape
    /// tests beside this one read a request by serializing it the second way.
    /// </summary>
    [Fact]
    public void TheGeneratedContextAndReflectionAgreeOnEveryField()
    {
        var request = LlamaCppRequestBuilder.Build(Backend, new LlamaCppOptions(), Conversation(), [Tool],
            stream: true, think: false);

        JsonSerializer.Serialize(request, LlmWireJsonContext.Default.LlamaCppChatRequest)
            .Should().Be(JsonSerializer.Serialize(request));
    }
}
