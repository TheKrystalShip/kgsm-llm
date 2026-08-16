using FluentAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// The hot-editable override layer: tool-description overlay from <c>tools.json</c> (names stay
/// structural, prose is replaced), with failure isolation — absent dir / absent file / bad JSON all
/// fall back to the in-code definitions rather than throwing or blanking.
/// </summary>
public sealed class FilePromptOverridesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kgsm-ovr-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort */ }
    }

    private FilePromptOverrides Make(bool withDir = true)
    {
        var settings = new Dictionary<string, string?>();
        if (withDir)
            settings[FilePromptOverrides.DirectoryKey] = _dir;
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new FilePromptOverrides(config, NullLogger<FilePromptOverrides>.Instance);
    }

private static IReadOnlyList<LlmToolDefinition> SampleTools() => new[]
    {
        LlmToolDefinition.Create(new Tool("get_status"), "BASE status desc",
            new LlmToolParameter("instance_name", "BASE param desc", Required: false)),
        LlmToolDefinition.Create(new Tool("list_blueprints"), "BASE list desc"),
    };
}
