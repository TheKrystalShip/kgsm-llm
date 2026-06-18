using System.Collections.Concurrent;

using FluentAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.Kgsm.Assistant;
using TheKrystalShip.Kgsm.Assistant.Envelope;
using TheKrystalShip.Kgsm.Assistant.Health;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;

using Xunit;
using Xunit.Abstractions;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

/// <summary>
/// Live end-to-end coverage for the <c>run_health_check</c> aggregator (toolbox-plan §3.4),
/// booting the REAL composition root (real KGSM.Lib → dev-checkout kgsm, real Ollama).
///
/// Two layers, deliberately separated (mirrors <see cref="ConfigEditLiveTests"/>):
///  • <see cref="HealthCheck_DirectSnapshot_RunsRealSweep"/> is DETERMINISTIC — it calls the port
///    (<see cref="IServerOperations.GetHealthSnapshotAsync"/>) directly and runs the pure
///    <see cref="HealthCheckAggregator"/>, proving the real fetch+map+synthesis WITHOUT depending
///    on the model. Its assertions are state-agnostic: liveness is never a failure (the §D5
///    stopped-≠-unhealthy invariant, true whether factorio-test is up or down) and host disk is
///    really measured (never a fabricated skip on the dev host).
///  • <see cref="HealthCheckPrompt_SelectsRunHealthCheck"/> adds model tool-SELECTION on top, across
///    both GPU-resident models: "is X healthy?" must route to run_health_check, and the tool's
///    recorded output must carry the deterministic summary.
///
/// Gated: a no-op unless <c>KGSM_LIVE_OLLAMA=1</c>. Run with:
///   KGSM_LIVE_OLLAMA=1 dotnet test --filter FullyQualifiedName~HealthCheckLiveTests
/// </summary>
public sealed class HealthCheckLiveTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string DevKgsm = "/home/heisen/tks/kgsm/kgsm.sh";
    private const string Instance = "factorio-test";
    private const string DefaultModel = "gemma4:12b";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly ITestOutputHelper _out;

    public HealthCheckLiveTests(WebApplicationFactory<Program> factory, ITestOutputHelper output)
    {
        _factory = factory;
        _out = output;
    }

    /// <summary>
    /// THE SWEEP PROOF (deterministic — no model). Fetches the real snapshot via the port and
    /// runs the aggregator against the live factorio-test. Assertions hold in BOTH running and
    /// stopped states: the four checks exist, liveness is never a failure (§D5), and the host
    /// disk check is genuinely measured (a real % on this host), never a fabricated skip.
    /// </summary>
    [Fact]
    public async Task HealthCheck_DirectSnapshot_RunsRealSweep()
    {
        if (!LiveEnabled()) return;

        var ops = WithDevKgsm().Services.GetRequiredService<IServerOperations>();

        var snapshot = await ops.GetHealthSnapshotAsync(Instance);
        snapshot.IsSuccess.Should().BeTrue("the real instance status must be readable");

        var result = HealthCheckAggregator.Run(snapshot.Value!, Instance);

        result.Subject.Should().Be(new ResultRef(ResourceKind.Server, Instance));
        result.Data.Checks.Select(c => c.Name)
            .Should().BeEquivalentTo(new[] { "liveness", "logs", "updates", "disk" });

        // §D5: a stateless engine can't call "stopped" unhealthy — liveness is never Fail.
        result.Data.Checks.Single(c => c.Name == "liveness").State.Should().Be(CheckState.Pass);
        result.Data.Overall.Should().NotBe(CheckState.Fail,
            "neither a stopped instance nor a healthy running one should read as a failure");

        // Host disk is real on the dev host → the disk check is measured, not skipped.
        result.Data.Checks.Single(c => c.Name == "disk").State.Should().NotBe(CheckState.Skip);

        result.Summary.Should().Contain(Instance);
        _out.WriteLine($"running={snapshot.Value!.Running}");
        _out.WriteLine(result.Summary);
    }

    [Theory]
    [InlineData("gemma4:12b")]
    [InlineData("qwen3.5:9b")]
    public async Task HealthCheckPrompt_SelectsRunHealthCheck(string model)
    {
        if (!LiveEnabled()) return;

        var (result, calls) = await RunTurnAsync(
            $"Is the {Instance} server healthy?", model);

        result.IsSuccess.Should().BeTrue();

        var health = calls.FirstOrDefault(c => c.Name == LlmTools.RunHealthCheck);
        health.Should().NotBeNull("a health question must route to run_health_check");
        (health!.Result ?? string.Empty).Should().Contain(Instance,
            "the tool's deterministic summary names the instance it assessed");
    }

    // --- harness ---------------------------------------------------------------------------

    private bool LiveEnabled()
    {
        if (Environment.GetEnvironmentVariable("KGSM_LIVE_OLLAMA") == "1") return true;
        _out.WriteLine("SKIPPED: set KGSM_LIVE_OLLAMA=1 (needs Ollama + dev-checkout kgsm + factorio-test).");
        return false;
    }

    private WebApplicationFactory<Program> WithDevKgsm() =>
        _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("KGSM:Path", DevKgsm);
            b.UseSetting("Ollama:Model", DefaultModel);
        });

    private WebApplicationFactory<Program> BuildFactory(ToolCallRecorder recorder, string model) =>
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
        string prompt, string model)
    {
        var recorder = new ToolCallRecorder();
        var assistant = BuildFactory(recorder, model).Services.GetRequiredService<IServerAssistant>();
        var result = await assistant.RunAsync(
            conversationId: $"live-health-{model}-{prompt.GetHashCode()}",
            userPrompt: prompt,
            canPerformActions: false); // run_health_check is a read — no authorization needed

        var calls = recorder.Calls;
        _out.WriteLine($"[{model}] prompt: {prompt}");
        _out.WriteLine($"  success={result.IsSuccess}");
        _out.WriteLine($"  tool calls: [{string.Join(", ", calls.Select(c => c.Describe()))}]");
        _out.WriteLine($"  reply: {result.Text}");
        return (result, calls);
    }

    private sealed record RecordedCall(Tool Name, IReadOnlyDictionary<string, string?> Args, string? Result)
    {
        public string Describe() => Args.Count == 0 ? Name.Name
            : $"{Name.Name}({string.Join(", ", Args.Select(kv => $"{kv.Key}={kv.Value ?? "null"}"))})";
    }

    private sealed class ToolCallRecorder
    {
        private readonly ConcurrentQueue<RecordedCall> _calls = new();
        public void Record(LlmToolCall call, string? result) =>
            _calls.Enqueue(new RecordedCall(call.Name, call.Arguments, result));
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

        public async Task<string> ExecuteAsync(LlmToolCall call, CancellationToken cancellationToken = default)
        {
            var result = await _inner.ExecuteAsync(call, cancellationToken);
            _recorder.Record(call, result);
            return result;
        }
    }
}
