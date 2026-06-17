using System.Text;

using FluentAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.Kgsm.Assistant.Extensions;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Extensions;
using TheKrystalShip.Llm.Extensions;

using Xunit.Abstractions;

namespace TheKrystalShip.Kgsm.Assistant.Cli.Tests;

/// <summary>
/// End-to-end proof that the CLI's three-call backend (AddLocalLlm + AddKgsmAssistant +
/// AddKgsmAdapters) composes and streams a real turn against live Ollama + kgsm — the same wiring
/// Program.cs uses. Read-only, so it never stages an action.
/// <para>
/// Gated: a no-op unless <c>KGSM_LIVE_OLLAMA=1</c> (mirrors the existing live-test convention), so
/// CI without a model stays green. Run with:
///   KGSM_LIVE_OLLAMA=1 dotnet test --filter FullyQualifiedName~CliLiveSmokeTests
/// </para>
/// </summary>
public class CliLiveSmokeTests
{
    private readonly ITestOutputHelper _output;
    public CliLiveSmokeTests(ITestOutputHelper output) => _output = output;

    private static bool Enabled => Environment.GetEnvironmentVariable("KGSM_LIVE_OLLAMA") == "1";

    [Fact]
    public async Task ReadOnlyTurn_StreamsAReply_AgainstLiveOllamaAndKgsm()
    {
        if (!Enabled)
            return;   // gated no-op — see class summary

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KGSM:Path"] = Environment.GetEnvironmentVariable("KGSM__Path") ?? "/opt/kgsm/kgsm.sh",
            })
            .AddEnvironmentVariables()
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        // The real host (HostApplicationBuilder) registers IConfiguration in DI; a bare
        // ServiceCollection must do it by hand (SystemPromptBuilder takes an IConfiguration).
        services.AddSingleton<IConfiguration>(config);
        services.AddLocalLlm(config);       // the SAME three calls Program.cs makes
        services.AddKgsmAssistant();
        services.AddKgsmAdapters(config);
        using var provider = services.BuildServiceProvider();

        var assistant = provider.GetRequiredService<IServerAssistant>();

        var reply = new StringBuilder();
        var sawFinal = false;
        await foreach (var ev in assistant.RunStreamAsync(
                           $"cli-test:{Guid.NewGuid():N}", "list the installed game servers",
                           canPerformActions: false))
        {
            switch (ev.Kind)
            {
                case AssistantEventKind.Token: reply.Append(ev.Text); break;
                case AssistantEventKind.Final: sawFinal = true; break;
                case AssistantEventKind.Error: throw new Xunit.Sdk.XunitException($"turn errored: {ev.ErrorMessage}");
            }
        }

        _output.WriteLine(reply.ToString());
        sawFinal.Should().BeTrue("the turn should end with a Final event");
        reply.ToString().Trim().Should().NotBeNullOrEmpty("the model should produce a reply");
    }
}
