using FluentAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// <see cref="PromptScaffold.WriteDefaults"/> seeds editable defaults (never clobbering edits), and
/// the <c>tools.json</c> it writes round-trips through <see cref="FilePromptOverrides"/> — proving the
/// seeded template is in the exact shape the override reader consumes.
/// </summary>
public sealed class PromptScaffoldTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kgsm-scaffold-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void WriteDefaults_SeedsAllFiles_FromConstants()
    {
        var result = PromptScaffold.WriteDefaults(_dir);

        result.Written.Should().HaveCount(5);   // 4 segments + tools.json
        result.Skipped.Should().BeEmpty();

        File.ReadAllText(Path.Combine(_dir, "preamble.md")).Should().Contain(KgsmAssistantPrompts.Preamble);
        File.ReadAllText(Path.Combine(_dir, "actions-allowed.md")).Should().Contain(KgsmAssistantPrompts.ActionsAllowed);
        File.ReadAllText(Path.Combine(_dir, "actions-auto.md")).Should().Contain(KgsmAssistantPrompts.ActionsAuto);
        File.ReadAllText(Path.Combine(_dir, "actions-denied.md")).Should().Contain(KgsmAssistantPrompts.ActionsDenied);
        File.Exists(Path.Combine(_dir, "tools.json")).Should().BeTrue();
    }

    [Fact]
    public void WriteDefaults_NeverClobbersExistingFiles()
    {
        Directory.CreateDirectory(_dir);
        var preamble = Path.Combine(_dir, "preamble.md");
        File.WriteAllText(preamble, "MY EDITS");

        var result = PromptScaffold.WriteDefaults(_dir);

        File.ReadAllText(preamble).Should().Be("MY EDITS");          // untouched
        result.Skipped.Should().Contain(preamble);
        result.Written.Should().HaveCount(4);                        // the other four
    }

    [Fact]
    public void SeededToolsJson_RoundTripsThroughTheOverrideReader()
    {
        PromptScaffold.WriteDefaults(_dir);

        // The seeded tools.json, read back by FilePromptOverrides, reproduces the base descriptions
        // (proving the seed shape matches the consumer) — and an edit to it takes effect.
        var json = File.ReadAllText(Path.Combine(_dir, "tools.json"));
        json.Should().Contain("get_status").And.Contain("instance_name");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [FilePromptOverrides.DirectoryKey] = _dir })
            .Build();
        var overrides = new FilePromptOverrides(config, NullLogger<FilePromptOverrides>.Instance);

        var overlaid = overrides.OverlayTools(LlmTools.ReadOnly);
        var getStatus = overlaid.Single(t => t.Name == "get_status");
        getStatus.Description.Should().Be(LlmTools.ReadOnly.Single(t => t.Name == "get_status").Description);
    }
}
