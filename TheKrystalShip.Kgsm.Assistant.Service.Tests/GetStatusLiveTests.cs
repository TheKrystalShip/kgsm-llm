using System.Collections.Concurrent;

using FluentAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.Kgsm.Assistant;
using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;

using Xunit;
using Xunit.Abstractions;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

/// <summary>
/// Live end-to-end check for the merged <c>get_status</c> tool: boots the REAL composition
/// root (real Ollama client, real kgsm) via <see cref="WebApplicationFactory{T}"/> and calls
/// <see cref="IServerAssistant.RunAsync"/> in-process, bypassing the OAuth-gated HTTP layer.
///
/// What this proves (and what it does NOT): the system prompt injects only instance NAMES,
/// not runtime status, so a "is it running?" question can only be answered by calling a tool.
/// Two distinct routes share the one <c>get_status</c> tool name and must be told apart by the
/// <c>instance_name</c> ARGUMENT (recorded below), not the name alone:
///   • no <c>instance_name</c>  → <c>GetFleetStatusAsync</c> → bulk <c>GetAllStatuses</c> — the
///     actual MaxIterations fix (the original bug was the FLEET question looping per instance);
///   • with <c>instance_name</c> → single-server detail (never the problem path).
/// So the fleet-phrased test asserts the recorded call carried NO <c>instance_name</c>.
///
/// This is an integration check, NOT a reproduction of the old MaxIterations bug: the
/// per-instance status tools (<c>is_server_active</c> et al.) were removed, so a status loop is
/// structurally impossible regardless of fleet size (unit-proven in ToolDispatcherTests), and a
/// single installed instance cannot recreate the original >7-instance loop.
///
/// Gated: requires Ollama running, the dev-checkout kgsm, and an installed instance, so it is a
/// no-op (asserts nothing) unless <c>KGSM_LIVE_OLLAMA=1</c>. Run with:
///   KGSM_LIVE_OLLAMA=1 dotnet test --filter FullyQualifiedName~GetStatusLiveTests
/// </summary>
public sealed class GetStatusLiveTests : IClassFixture<WebApplicationFactory<Program>>
{
    // The canonical dev checkout (see the "kgsm dev checkout is canonical" project note);
    // the service's appsettings default of /opt/kgsm is the old version.
    private const string DevKgsm = "/home/heisen/tks/kgsm/kgsm.sh";

    private const string IterationLimitReply =
        "I wasn't able to finish that after a few steps — could you rephrase or break it down?";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly ITestOutputHelper _out;

    public GetStatusLiveTests(WebApplicationFactory<Program> factory, ITestOutputHelper output)
    {
        _factory = factory;
        _out = output;
    }

    /// <summary>
    /// FLEET path — the actual MaxIterations fix. A plural, no-name prompt must drive a single
    /// <c>get_status</c> call with NO <c>instance_name</c> (→ <c>GetFleetStatusAsync</c> → bulk).
    /// </summary>
    [Theory]
    [InlineData("gemma4:12b")]   // the configured default model
    [InlineData("qwen3.5:9b")]   // cheap second data point
    public async Task FleetPrompt_RoutesToSingleBulkGetStatus_NoInstanceName(string model)
    {
        if (!LiveEnabled()) return;

        var (result, calls) = await RunTurnAsync(model, "Which of my game servers are currently running?");

        result.IsSuccess.Should().BeTrue("the live turn should complete against Ollama + kgsm");
        result.Text.Should().NotBe(IterationLimitReply, "the loop must not hit the MaxIterations cap");

        var statusCalls = calls.Where(c => c.Name == LlmTools.GetStatus).ToList();
        statusCalls.Should().ContainSingle(
            "a fleet status question should need exactly one bulk get_status call — no per-instance looping");

        // The dispatcher routes to the bulk fleet read precisely when instance_name is blank
        // (ToolDispatcher.GetStatusAsync: IsNullOrWhiteSpace → GetFleetStatusAsync).
        var instanceArg = statusCalls[0].Args.GetValueOrDefault("instance_name");
        string.IsNullOrWhiteSpace(instanceArg).Should().BeTrue(
            $"the fleet prompt must take the bulk path (no instance_name); model passed instance_name='{instanceArg}'");

        result.Text.ToLowerInvariant().Should().Contain("factorio-test",
            "the one installed instance should appear in the fleet summary");
    }

    /// <summary>
    /// SINGLE path — named server. Honest scope: this is NOT the bug path; it only asserts the
    /// model answers live status with one get_status call (route may be single or bulk).
    /// </summary>
    [Theory]
    [InlineData("gemma4:12b")]
    [InlineData("qwen3.5:9b")]
    public async Task NamedPrompt_AnswersSingleServerStatus(string model)
    {
        if (!LiveEnabled()) return;

        var (result, calls) = await RunTurnAsync(model, "Is my factorio-test server running right now?");

        result.IsSuccess.Should().BeTrue();
        result.Text.Should().NotBe(IterationLimitReply);
        calls.Count(c => c.Name == LlmTools.GetStatus).Should().BeInRange(1, 2,
            "answering live status needs get_status (status isn't in the system prompt); no looping");

        // factorio-test is installed but stopped, so the model should report it as not running.
        result.Text.ToLowerInvariant().Should()
            .MatchRegex("not running|stopped|offline|inactive|isn't running|is not");
    }

    /// <summary>
    /// Streaming path: the live turn emits the canonical §5a vocabulary — a get_status tool round
    /// (tool.start with NO instance_name → bulk path, then tool.result) and a terminal Final, with
    /// no iteration-cap apology. Like the buffered live tests, this needs an installed instance:
    /// with an empty fleet the model can answer "(none)" straight from the injected inventory and
    /// skip the tool call, which would fail the get_status assertion.
    /// </summary>
    [Theory]
    [InlineData("gemma4:12b")]
    [InlineData("qwen3.5:9b")]
    public async Task FleetPrompt_StreamsCanonicalTypedEvents(string model)
    {
        if (!LiveEnabled()) return;

        var events = await RunStreamTurnAsync(model, "Which of my game servers are currently running?");

        var toolStarts = events.Where(e => e.Kind == AssistantEventKind.ToolStart).ToList();
        toolStarts.Should().Contain(e => e.ToolName == LlmTools.GetStatus,
            "a fleet status question drives a get_status tool round on the stream");
        events.Should().Contain(e => e.Kind == AssistantEventKind.ToolResult,
            "each tool.start is followed by a tool.result");
        events.Should().NotBeEmpty();
        events[^1].Kind.Should().Be(AssistantEventKind.Final, "the stream ends with exactly one terminal Final");
        events[^1].Text.Should().NotBe(IterationLimitReply, "the loop must not hit the MaxIterations cap");

        // The bulk path: the get_status tool.start carried no instance_name.
        var fleetStart = toolStarts.First(e => e.ToolName == LlmTools.GetStatus);
        string.IsNullOrWhiteSpace(fleetStart.ToolArguments?.GetValueOrDefault("instance_name")).Should().BeTrue(
            "the fleet stream must take the bulk path (no instance_name)");
    }

    /// <summary>
    /// §3.5/§4.1 command path — an authorized lifecycle prompt must PROPOSE, not execute: the
    /// turn stages the matching confirmation for the resolved instance (drained into
    /// <see cref="AssistantResult.Confirmations"/>) and proposes via the merged
    /// <c>server_command</c> tool with the right <c>verb</c>. Two verbs across both models check
    /// the §3.2 thesis that consolidation keeps model SELECTION reliable (not just routing).
    /// factorio-test must be installed (and is stopped), so the prompt has a real, unambiguous
    /// target — staging is propose-only, so "stop/restart" on a stopped server still only stages.
    /// (The "never executes inline" guarantee itself is unit-proven in ToolDispatcherTests via
    /// DidNotReceive().StartAsync; this live test confirms the model selects + stages it.)
    /// </summary>
    [Theory]
    [InlineData("gemma4:12b", "Start the factorio-test server.", "start", ConfirmationKind.Start)]
    [InlineData("gemma4:12b", "Restart the factorio-test server.", "restart", ConfirmationKind.Restart)]
    [InlineData("qwen3.5:9b", "Start the factorio-test server.", "start", ConfirmationKind.Start)]
    [InlineData("qwen3.5:9b", "Restart the factorio-test server.", "restart", ConfirmationKind.Restart)]
    public async Task CommandPrompt_StagesConfirmation_NeverExecutesInLoop(
        string model, string prompt, string verb, ConfirmationKind kind)
    {
        if (!LiveEnabled()) return;

        var (result, calls) = await RunTurnAsync(model, prompt, canPerformActions: true);

        result.IsSuccess.Should().BeTrue("the live turn should complete against Ollama + kgsm");
        result.Text.Should().NotBe(IterationLimitReply);

        // Propose-only: the action was staged for the resolved instance, awaiting human confirmation.
        result.Confirmations.Should().Contain(
            c => c.Kind == kind && c.Target == "factorio-test",
            $"an authorized {verb} request must STAGE a {kind} confirmation, not run it inline");

        // The model proposed via the merged server_command tool with the right verb (the
        // dispatcher routes the verb to its kind and only stages it).
        calls.Should().Contain(c =>
            c.Name == LlmTools.ServerCommand && c.Args.GetValueOrDefault("verb") == verb);
    }

    // --- harness ---------------------------------------------------------------------------

    private bool LiveEnabled()
    {
        if (Environment.GetEnvironmentVariable("KGSM_LIVE_OLLAMA") == "1")
            return true;
        // xUnit 2.9.2 has no runtime Assert.Skip; gate by env so the suite stays green offline.
        _out.WriteLine("SKIPPED: set KGSM_LIVE_OLLAMA=1 (needs Ollama + dev-checkout kgsm + an installed instance).");
        return false;
    }

    /// <summary>
    /// Boots the real composition root with the dev-checkout kgsm + the chosen model, and wraps the
    /// app's IToolDispatcher with a recorder (capturing tool name + arguments) without naming the
    /// internal type.
    /// </summary>
    private WebApplicationFactory<Program> BuildFactory(string model, ToolCallRecorder recorder) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("KGSM:Path", DevKgsm);
            builder.UseSetting("Ollama:Model", model);
            builder.ConfigureTestServices(services =>
            {
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

    private async Task<(AssistantResult Result, IReadOnlyList<RecordedCall> Calls)> RunTurnAsync(
        string model, string prompt, bool canPerformActions = false)
    {
        var recorder = new ToolCallRecorder();
        var assistant = BuildFactory(model, recorder).Services.GetRequiredService<IServerAssistant>();
        var result = await assistant.RunAsync(
            conversationId: $"live-{model}-{prompt.GetHashCode()}",
            userPrompt: prompt,
            canPerformActions: canPerformActions);

        var calls = recorder.Calls;
        _out.WriteLine($"[model={model}] prompt: {prompt}");
        _out.WriteLine($"  success={result.IsSuccess}");
        _out.WriteLine($"  tool calls: [{string.Join(", ", calls.Select(c => c.Describe()))}]");
        _out.WriteLine($"  reply: {result.Text}");
        return (result, calls);
    }

    private async Task<IReadOnlyList<AssistantStreamEvent>> RunStreamTurnAsync(string model, string prompt)
    {
        var recorder = new ToolCallRecorder();
        var assistant = BuildFactory(model, recorder).Services.GetRequiredService<IServerAssistant>();
        var events = new List<AssistantStreamEvent>();
        await foreach (var ev in assistant.RunStreamAsync(
                           $"live-stream-{model}-{prompt.GetHashCode()}", prompt, canPerformActions: false))
            events.Add(ev);

        _out.WriteLine($"[stream model={model}] prompt: {prompt}");
        _out.WriteLine($"  events: [{string.Join(", ", events.Select(e => e.Kind))}]");
        return events;
    }

    private sealed record RecordedCall(string Name, IReadOnlyDictionary<string, string?> Args)
    {
        public string Describe() =>
            Args.Count == 0
                ? Name
                : $"{Name}({string.Join(", ", Args.Select(kv => $"{kv.Key}={kv.Value ?? "null"}"))})";
    }

    /// <summary>Thread-safe sink for the tool calls (name + args) the dispatcher saw, in order.</summary>
    private sealed class ToolCallRecorder
    {
        private readonly ConcurrentQueue<RecordedCall> _calls = new();
        public void Record(LlmToolCall call) => _calls.Enqueue(new RecordedCall(call.Name, call.Arguments));
        public IReadOnlyList<RecordedCall> Calls => _calls.ToArray();
    }

    /// <summary>Pass-through dispatcher that records each call before delegating.</summary>
    private sealed class RecordingToolDispatcher : IToolDispatcher
    {
        private readonly IToolDispatcher _inner;
        private readonly ToolCallRecorder _recorder;

        public RecordingToolDispatcher(IToolDispatcher inner, ToolCallRecorder recorder)
        {
            _inner = inner;
            _recorder = recorder;
        }

        public Task<string> ExecuteAsync(LlmToolCall call, CancellationToken cancellationToken = default)
        {
            _recorder.Record(call);
            return _inner.ExecuteAsync(call, cancellationToken);
        }
    }
}
