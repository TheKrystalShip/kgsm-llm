using System.Text.Json;

using FluentAssertions;

using TheKrystalShip.Llm.Backends;
using TheKrystalShip.Llm.Backends.LlamaCpp;
using TheKrystalShip.Llm.Models;

using Xunit;

namespace TheKrystalShip.Llm.Tests;

/// <summary>
/// Request-shape tests for <see cref="LlamaCppRequestBuilder"/>. The two things that differ from
/// Ollama's native body are both correctness-critical: arguments travel as a JSON string, and a
/// tool result is addressed by the id of the call it answers — an id <see cref="LlmMessage"/> does
/// not carry and the builder therefore has to derive.
/// </summary>
public class LlamaCppRequestBuilderTests
{
    private static readonly LlmBackendOptions Backend = new()
    {
        Model = "gemma4:12b",
        ContextWindow = 32768,
        Temperature = 0.3
    };

    private static readonly LlamaCppOptions LlamaCpp = new();

    private static JsonElement Build(
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmToolDefinition>? tools = null,
        bool stream = false,
        bool think = false,
        LlamaCppOptions? llamaCpp = null) =>
        JsonSerializer.SerializeToElement(
            LlamaCppRequestBuilder.Build(Backend, llamaCpp ?? LlamaCpp, messages, tools, stream, think));

    [Fact]
    public void ContextWindow_IsNeverSent_BecauseTheServerFixesItAtLaunch()
    {
        var body = Build([LlmMessage.User("hi")]);

        body.TryGetProperty("num_ctx", out _).Should().BeFalse();
        body.TryGetProperty("n_ctx", out _).Should().BeFalse();
        body.GetProperty("model").GetString().Should().Be("gemma4:12b");
        body.GetProperty("temperature").GetDouble().Should().Be(0.3);
    }

    [Fact]
    public void Streaming_AsksForUsage_OrTokenCountsWouldNeverArrive()
    {
        Build([LlmMessage.User("hi")], stream: true)
            .GetProperty("stream_options").GetProperty("include_usage").GetBoolean()
            .Should().BeTrue();

        Build([LlmMessage.User("hi")], stream: false)
            .TryGetProperty("stream_options", out _).Should().BeFalse();
    }

    [Fact]
    public void Seed_IsSentOnlyWhenConfigured()
    {
        Build([LlmMessage.User("hi")]).TryGetProperty("seed", out _)
            .Should().BeFalse("an absent seed leaves the backend's own unseeded sampling alone");

        var seeded = new LlmBackendOptions { Model = "m", Seed = 42 };
        JsonSerializer.SerializeToElement(
                LlamaCppRequestBuilder.Build(seeded, LlamaCpp, [LlmMessage.User("hi")], null, false, false))
            .GetProperty("seed").GetInt32().Should().Be(42);
    }

    [Fact]
    public void ToolCallArguments_AreSerialisedAsAJsonString()
    {
        var call = new LlmToolCall(new Tool("get_server_status"),
            new Dictionary<string, string?> { ["instance"] = "terraria" });

        var body = Build([
            LlmMessage.User("status?"),
            LlmMessage.AssistantToolCalls([call]),
            LlmMessage.Tool(new Tool("get_server_status"), "running")
        ]);

        var arguments = body.GetProperty("messages")[1]
            .GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments");

        arguments.ValueKind.Should().Be(JsonValueKind.String, "the OpenAI wire format nests JSON as text");
        JsonDocument.Parse(arguments.GetString()!).RootElement
            .GetProperty("instance").GetString().Should().Be("terraria");
    }

    [Fact]
    public void ToolResult_CarriesTheIdOfTheCallItAnswers()
    {
        var body = Build([
            LlmMessage.User("status?"),
            LlmMessage.AssistantToolCalls([
                new LlmToolCall(new Tool("get_server_status"), new Dictionary<string, string?>())
            ]),
            LlmMessage.Tool(new Tool("get_server_status"), "running")
        ]);

        var messages = body.GetProperty("messages");
        var callId = messages[1].GetProperty("tool_calls")[0].GetProperty("id").GetString();

        messages[2].GetProperty("role").GetString().Should().Be("tool");
        messages[2].GetProperty("tool_call_id").GetString().Should().Be(callId);
    }

    [Fact]
    public void SeveralCallsInOneRound_AreMatchedToResultsByName()
    {
        var body = Build([
            LlmMessage.User("both?"),
            LlmMessage.AssistantToolCalls([
                new LlmToolCall(new Tool("alpha"), new Dictionary<string, string?>()),
                new LlmToolCall(new Tool("beta"), new Dictionary<string, string?>())
            ]),
            // Deliberately answered out of order — a result names its tool, never a position.
            LlmMessage.Tool(new Tool("beta"), "b"),
            LlmMessage.Tool(new Tool("alpha"), "a")
        ]);

        var messages = body.GetProperty("messages");
        var alphaId = messages[1].GetProperty("tool_calls")[0].GetProperty("id").GetString();
        var betaId = messages[1].GetProperty("tool_calls")[1].GetProperty("id").GetString();

        messages[2].GetProperty("tool_call_id").GetString().Should().Be(betaId);
        messages[3].GetProperty("tool_call_id").GetString().Should().Be(alphaId);
    }

    [Fact]
    public void RepeatedCallsToOneTool_ClaimTheOldestOutstandingIdFirst()
    {
        var body = Build([
            LlmMessage.AssistantToolCalls([new LlmToolCall(new Tool("probe"), new Dictionary<string, string?>())]),
            LlmMessage.Tool(new Tool("probe"), "first"),
            LlmMessage.AssistantToolCalls([new LlmToolCall(new Tool("probe"), new Dictionary<string, string?>())]),
            LlmMessage.Tool(new Tool("probe"), "second")
        ]);

        var messages = body.GetProperty("messages");
        messages[1].GetProperty("tool_call_id").GetString()
            .Should().Be(messages[0].GetProperty("tool_calls")[0].GetProperty("id").GetString());
        messages[3].GetProperty("tool_call_id").GetString()
            .Should().Be(messages[2].GetProperty("tool_calls")[0].GetProperty("id").GetString());
    }

    [Fact]
    public void AToolResultWhoseCallFellOutOfTheWindow_StillGetsAnId()
    {
        // A trimmed history can replay a result whose call is no longer present. Dropping the id
        // would make the whole request invalid, so the oldest outstanding one is claimed instead.
        var body = Build([
            LlmMessage.AssistantToolCalls([new LlmToolCall(new Tool("alpha"), new Dictionary<string, string?>())]),
            LlmMessage.Tool(new Tool("vanished"), "orphan")
        ]);

        body.GetProperty("messages")[1].TryGetProperty("tool_call_id", out var id).Should().BeTrue();
        id.GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void AToolResultWithNothingOutstanding_OmitsTheIdRatherThanInventingOne()
    {
        var body = Build([LlmMessage.Tool(new Tool("alpha"), "orphan")]);

        body.GetProperty("messages")[0].TryGetProperty("tool_call_id", out _).Should().BeFalse();
    }

    [Fact]
    public void Tools_AreSentWithTheSharedSchema_AndParallelCallsAreOffByDefault()
    {
        var definition = LlmToolDefinition.Create(
            new Tool("server_command"), "Acts on a server",
            new LlmToolParameter("verb", "What to do", AllowedValues: ["start", "stop"]));

        var body = Build([LlmMessage.User("go")], [definition]);

        var function = body.GetProperty("tools")[0].GetProperty("function");
        function.GetProperty("name").GetString().Should().Be("server_command");
        function.GetProperty("parameters").GetProperty("properties").GetProperty("verb")
            .GetProperty("enum").EnumerateArray().Select(v => v.GetString())
            .Should().Equal("start", "stop");

        body.GetProperty("parallel_tool_calls").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void Thinking_ReachesTheTemplateVariableInBothStates()
    {
        Build([LlmMessage.User("hi")], think: true)
            .GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean()
            .Should().BeTrue();

        // Off is SENT, not left unsaid. llama-server's --reasoning defaults to `auto`, which turns
        // reasoning on when the template supports it, so an absent variable reads as enabled and the
        // model reasons on every turn regardless of this flag.
        Build([LlmMessage.User("hi")], think: false)
            .GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean()
            .Should().BeFalse();

        // A template that spells it differently is configured, not code-changed.
        Build([LlmMessage.User("hi")], think: true, llamaCpp: new LlamaCppOptions { ThinkingTemplateKwarg = "reasoning" })
            .GetProperty("chat_template_kwargs").GetProperty("reasoning").GetBoolean()
            .Should().BeTrue();

        // A template declaring no such variable is told nothing at all.
        Build([LlmMessage.User("hi")], think: false, llamaCpp: new LlamaCppOptions { ThinkingTemplateKwarg = "" })
            .TryGetProperty("chat_template_kwargs", out _).Should().BeFalse();
    }

    [Fact]
    public void Dry_IsSentByDefault_SoARepetitionLoopIsBounded()
    {
        var body = Build([LlmMessage.User("hi")]);

        body.GetProperty("dry_multiplier").GetDouble().Should().Be(0.8);
        body.GetProperty("dry_base").GetDouble().Should().Be(1.75);
        body.GetProperty("dry_allowed_length").GetInt32().Should().Be(4);
        body.GetProperty("dry_penalty_last_n").GetInt32().Should().Be(1024);
    }

    [Fact]
    public void Dry_IsOmittedEntirely_WhenTurnedOff()
    {
        var body = Build([LlmMessage.User("hi")], llamaCpp: new LlamaCppOptions { DryMultiplier = 0 });

        body.TryGetProperty("dry_multiplier", out _).Should().BeFalse();
        body.TryGetProperty("dry_base", out _).Should().BeFalse();
        body.TryGetProperty("dry_allowed_length", out _).Should().BeFalse();
        body.TryGetProperty("dry_penalty_last_n", out _).Should().BeFalse();
    }
}
