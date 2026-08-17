using System.Collections.Concurrent;

using FluentAssertions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;

using Xunit.Abstractions;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

/// <summary>
/// The model-in-the-loop proof for web_search: boots the REAL composition root (real Ollama
/// model, real TavilyWebSearch) and runs a turn, asserting the model actually EMITS a web_search
/// tool call and the turn completes — i.e. "Gemma can search", not just "Tavily responds". The
/// kgsm inventory is faked (web_search doesn't touch kgsm), so this needs only Ollama + a Tavily
/// key, not a live kgsm. Spends one Tavily credit per run.
/// <para>
/// Gated: a no-op unless <c>KGSM_LIVE_OLLAMA=1</c> AND <c>WebSearch__ApiKey</c> are set. Run with:
///   KGSM_LIVE_OLLAMA=1 WebSearch__ApiKey=tvly-... \
///     dotnet test --filter FullyQualifiedName~WebSearchLiveTests
/// </para>
/// </summary>
public sealed class WebSearchLiveTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string IterationLimitReply =
        "I wasn't able to finish that after a few steps — could you rephrase or break it down?";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly ITestOutputHelper _out;

    public WebSearchLiveTests(WebApplicationFactory<Program> factory, ITestOutputHelper output)
    {
        _factory = factory;
        _out = output;
    }

    [Theory]
    [InlineData("gemma4:12b")] // the configured default model
    public async Task WebLookupPrompt_DrivesAWebSearchCall_AndAnswers(string model)
    {
        if (!LiveEnabled(out var apiKey)) return;

        var recorder = new ToolCallRecorder();
        var assistant = BuildFactory(model, apiKey, recorder).Services.GetRequiredService<IServerAssistant>();

        var result = await assistant.RunAsync(
            conversationId: $"live-websearch-{model}",
            userPrompt: "What is the latest released version of the game Terraria? Search the web to be sure.",
            canPerformActions: false);

        _out.WriteLine($"[model={model}] tool calls: [{string.Join(", ", recorder.Calls.Select(c => c.Describe()))}]");
        _out.WriteLine($"reply: {result.Text}");

        result.IsSuccess.Should().BeTrue("the live turn should complete against Ollama + Tavily");
        result.Text.Should().NotBe(IterationLimitReply, "the loop must not hit the MaxIterations cap");
        result.Text.Should().NotBeNullOrWhiteSpace();
        recorder.Calls.Should().Contain(c => c.Name == ShippedTextForTests.Name(LlmTools.Search),
            "an outside-fact prompt that explicitly asks to search the web should drive a search call");
    }

    // --- harness ---------------------------------------------------------------------------

    private bool LiveEnabled(out string apiKey)
    {
        apiKey = Environment.GetEnvironmentVariable("WebSearch__ApiKey") ?? string.Empty;
        if (Environment.GetEnvironmentVariable("KGSM_LIVE_OLLAMA") == "1" && !string.IsNullOrWhiteSpace(apiKey))
            return true;

        _out.WriteLine("SKIPPED: set KGSM_LIVE_OLLAMA=1 and WebSearch__ApiKey (needs Ollama + a Tavily key).");
        return false;
    }

    /// <summary>
    /// Real composition root with the chosen model + Tavily key. The kgsm inventory is faked (so the
    /// system prompt builds without a live kgsm — web_search needs none), and the app's
    /// IToolDispatcher is wrapped with a recorder to capture the tool calls the model emits.
    /// </summary>
    private WebApplicationFactory<Program> BuildFactory(string model, string apiKey, ToolCallRecorder recorder) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("KGSM:Path", "/home/heisen/tks/kgsm/kgsm.sh");
            builder.UseSetting("Llm:Model", model);
            builder.UseSetting("WebSearch:ApiKey", apiKey);
            builder.ConfigureTestServices(services =>
            {
                var inventory = Substitute.For<IServerInventory>();
                inventory.GetInstancesAsync(Arg.Any<CancellationToken>())
                    .Returns((IReadOnlyDictionary<string, string>)new Dictionary<string, string>());
                inventory.GetBlueprintNamesAsync(Arg.Any<CancellationToken>())
                    .Returns((IReadOnlyCollection<string>)Array.Empty<string>());
                services.AddSingleton(inventory);

                services.AddSingleton(recorder);
                var descriptor = services.Last(d => d.ServiceType == typeof(IToolDispatcher));
                services.Remove(descriptor);
                services.AddSingleton<IToolDispatcher>(sp =>
                {
                    var inner = (IToolDispatcher)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!);
                    return new RecordingToolDispatcher(inner, recorder);
                });
            });
        });

    private sealed record RecordedCall(Tool Name, IReadOnlyDictionary<string, string?> Args)
    {
        public string Describe() =>
            Args.Count == 0 ? Name.Name : $"{Name.Name}({string.Join(", ", Args.Select(kv => $"{kv.Key}={kv.Value ?? "null"}"))})";
    }

    private sealed class ToolCallRecorder
    {
        private readonly ConcurrentQueue<RecordedCall> _calls = new();
        public void Record(LlmToolCall call) => _calls.Enqueue(new RecordedCall(call.Name, call.Arguments));
        public IReadOnlyList<RecordedCall> Calls => _calls.ToArray();
    }

    private sealed class RecordingToolDispatcher : IToolDispatcher
    {
        private readonly IToolDispatcher _inner;
        private readonly ToolCallRecorder _recorder;

        public RecordingToolDispatcher(IToolDispatcher inner, ToolCallRecorder recorder)
        {
            _inner = inner;
            _recorder = recorder;
        }

        public Task<ToolOutput> ExecuteAsync(LlmToolCall call, CancellationToken cancellationToken = default)
        {
            _recorder.Record(call);
            return _inner.ExecuteAsync(call, cancellationToken);
        }
    }
}
