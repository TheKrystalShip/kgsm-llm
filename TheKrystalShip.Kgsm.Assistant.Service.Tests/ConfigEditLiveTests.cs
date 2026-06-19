using System.Collections.Concurrent;

using FluentAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.Kgsm.Assistant;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;

using Xunit;
using Xunit.Abstractions;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

/// <summary>
/// Live end-to-end coverage for the <c>.config.ini</c> editing lane (§3.8), booting the REAL
/// composition root (real KGSM.Lib → dev-checkout kgsm, real Ollama) via
/// <see cref="WebApplicationFactory{T}"/>.
///
/// Two layers, deliberately separated:
///  • <see cref="ViewConfigFile_DirectRead_ReturnsRealConfig"/> is DETERMINISTIC — it calls the
///    port (<see cref="IServerOperations.ReadInstanceFileAsync"/>) directly, so it proves the
///    view_config_file read-path FIX (resolve via kgsm's <c>instances find</c>/__find_instance_config,
///    not the file-less <c>install_dir</c>) WITHOUT depending on whether the model picks a tool.
///    This is the real regression proof.
///  • The two prompt tests add model tool-SELECTION on top: a view request must call
///    view_config_file (and the tool's output, captured below, must carry real config content);
///    an authorized "set X" request must STAGE a SetConfig confirmation — propose-only, no inline write.
///
/// Gated: requires Ollama running, the dev-checkout kgsm, and an installed <c>factorio-test</c>
/// instance, so every test is a no-op unless <c>KGSM_LIVE_OLLAMA=1</c>. Run with:
///   KGSM_LIVE_OLLAMA=1 dotnet test --filter FullyQualifiedName~ConfigEditLiveTests
/// </summary>
public sealed class ConfigEditLiveTests : IClassFixture<WebApplicationFactory<Program>>
{
    // The canonical dev checkout (see the "kgsm dev checkout is canonical" note); the
    // service's appsettings default of /opt/kgsm is the old version.
    private const string DevKgsm = "/home/heisen/tks/kgsm/kgsm.sh";
    private const string Instance = "factorio-test";
    private const string Model = "gemma4:12b"; // already GPU-resident on the dev host

    private readonly WebApplicationFactory<Program> _factory;
    private readonly ITestOutputHelper _out;

    public ConfigEditLiveTests(WebApplicationFactory<Program> factory, ITestOutputHelper output)
    {
        _factory = factory;
        _out = output;
    }

    /// <summary>
    /// THE FIX PROOF (deterministic — no model in the loop). Before the fix,
    /// <c>KgsmServerOperations</c> bound the read boundary to <c>install_dir</c>
    /// (…/&lt;inst&gt;/install) and 404'd for every real instance; now it resolves through
    /// kgsm's own __find_instance_config. Calls the port directly against the real instance.
    /// </summary>
    [Fact]
    public async Task ViewConfigFile_DirectRead_ReturnsRealConfig()
    {
        if (!LiveEnabled()) return;

        var ops = WithDevKgsm().Services.GetRequiredService<IServerOperations>();

        var result = await ops.ReadInstanceFileAsync(Instance, $"{Instance}.config.ini");

        result.IsSuccess.Should().BeTrue(
            "the config read must resolve via `instances find`, not the (file-less) install_dir");
        result.Value.Should().Contain("auto_update", "the real .config.ini content was read");
        _out.WriteLine(result.Value);
    }

    /// <summary>
    /// Model tool-selection on top of the fix: a view request must route to view_config_file,
    /// and the tool's recorded OUTPUT must carry real config content (deterministic given the
    /// call — the tool result is fixed by the fix, only the model's choice to call it varies).
    /// </summary>
    [Fact]
    public async Task ViewConfigPrompt_CallsViewConfigFile_AndToolReturnsContent()
    {
        if (!LiveEnabled()) return;

        var (result, calls) = await RunTurnAsync(
            $"Show me the configuration file for the {Instance} server.", canPerformActions: true);

        result.IsSuccess.Should().BeTrue();

        var view = calls.FirstOrDefault(c => c.Name == LlmTools.ViewConfigFile);
        view.Should().NotBeNull("a config-view request must route to view_config_file");
        (view!.Result ?? string.Empty).Should().Contain("auto_update",
            "the fixed read path must surface real config content to the model (not a 'could not read' error)");
    }

    /// <summary>
    /// Authorized "set X" must PROPOSE: stage a SetConfig confirmation for the resolved
    /// instance/key, never write inline. The actual write only happens on a separate confirm.
    /// </summary>
    [Fact]
    public async Task SetConfigPrompt_StagesConfirmation_NeverWritesInline()
    {
        if (!LiveEnabled()) return;

        var (result, calls) = await RunTurnAsync(
            $"Set the auto_update setting to true on the {Instance} server.", canPerformActions: true);

        result.IsSuccess.Should().BeTrue();
        calls.Should().Contain(c => c.Name == LlmTools.SetConfigValue);
        result.Confirmations.Should().Contain(
            c => c.Kind == ConfirmationKind.SetConfig && c.Target == Instance && c.ConfigKey == "auto_update",
            "an authorized set request must STAGE a SetConfig confirmation, not apply it");

        // Propose-only: nothing was written — kgsm still reports the pre-edit value.
        var current = await WithDevKgsm().Services.GetRequiredService<IServerOperations>()
            .ReadInstanceFileAsync(Instance, $"{Instance}.config.ini");
        (current.Value ?? string.Empty).Should().Contain("auto_update=\"false\"",
            "staging must not mutate the file — auto_update stays false until the edit is confirmed");
    }

    // --- harness ---------------------------------------------------------------------------

    private bool LiveEnabled()
    {
        if (Environment.GetEnvironmentVariable("KGSM_LIVE_OLLAMA") == "1") return true;
        // xUnit 2.x has no runtime Assert.Skip; gate by env so the suite stays green offline.
        _out.WriteLine("SKIPPED: set KGSM_LIVE_OLLAMA=1 (needs Ollama + dev-checkout kgsm + factorio-test).");
        return false;
    }

    /// <summary>Boots the real composition root pointed at the dev kgsm + the GPU-resident model.</summary>
    private WebApplicationFactory<Program> WithDevKgsm() =>
        _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("KGSM:Path", DevKgsm);
            b.UseSetting("Ollama:Model", Model);
        });

    /// <summary>As <see cref="WithDevKgsm"/>, but wraps the dispatcher to record each tool call + result.</summary>
    private WebApplicationFactory<Program> BuildFactory(ToolCallRecorder recorder) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("KGSM:Path", DevKgsm);
            builder.UseSetting("Ollama:Model", Model);
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
        string prompt, bool canPerformActions)
    {
        var recorder = new ToolCallRecorder();
        var assistant = BuildFactory(recorder).Services.GetRequiredService<IServerAssistant>();
        var result = await assistant.RunAsync(
            conversationId: $"live-cfg-{prompt.GetHashCode()}",
            userPrompt: prompt,
            canPerformActions: canPerformActions);

        var calls = recorder.Calls;
        _out.WriteLine($"prompt: {prompt}");
        _out.WriteLine($"  success={result.IsSuccess}");
        _out.WriteLine($"  tool calls: [{string.Join(", ", calls.Select(c => c.Describe()))}]");
        _out.WriteLine($"  confirmations: [{string.Join(", ", result.Confirmations.Select(c => $"{c.Kind}:{c.Target}:{c.ConfigKey}"))}]");
        _out.WriteLine($"  reply: {result.Text}");
        return (result, calls);
    }

    private sealed record RecordedCall(Tool Name, IReadOnlyDictionary<string, string?> Args, string? Result)
    {
        public string Describe() => Args.Count == 0 ? Name.Name
            : $"{Name.Name}({string.Join(", ", Args.Select(kv => $"{kv.Key}={kv.Value ?? "null"}"))})";
    }

    /// <summary>Thread-safe sink for the tool calls (name + args + result) the dispatcher saw, in order.</summary>
    private sealed class ToolCallRecorder
    {
        private readonly ConcurrentQueue<RecordedCall> _calls = new();
        public void Record(LlmToolCall call, string? result) =>
            _calls.Enqueue(new RecordedCall(call.Name, call.Arguments, result));
        public IReadOnlyList<RecordedCall> Calls => _calls.ToArray();
    }

    /// <summary>Pass-through dispatcher that records each call + its result before returning.</summary>
    private sealed class RecordingToolDispatcher : IToolDispatcher
    {
        private readonly IToolDispatcher _inner;
        private readonly ToolCallRecorder _recorder;

        public RecordingToolDispatcher(IToolDispatcher inner, ToolCallRecorder recorder)
        {
            _inner = inner;
            _recorder = recorder;
        }

        public async Task<ToolOutput> ExecuteAsync(LlmToolCall call, CancellationToken cancellationToken = default)
        {
            var result = await _inner.ExecuteAsync(call, cancellationToken);
            _recorder.Record(call, result.Summary);
            return result;
        }
    }
}
