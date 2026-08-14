using FluentAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Ports;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// The prompt text lives in the library (<see cref="KgsmAssistantPrompts"/>) so every host shares it
/// without copy-pasting config. These verify the precedence — editable file > inline Llm:* config >
/// lib constant — and that the recorded <see cref="BuiltPrompt.TemplateHash"/> tracks the EDITABLE
/// template (it moves when the persona changes, not when the injected live lists do).
/// </summary>
public sealed class SystemPromptBuilderTests : IDisposable
{
    private readonly IServerInventory _inventory = Substitute.For<IServerInventory>();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kgsm-prompts-" + Guid.NewGuid().ToString("N"));

    public SystemPromptBuilderTests()
    {
        _inventory.GetInstancesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>()));
        _inventory.GetBlueprintNamesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<string>>(Array.Empty<string>()));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private SystemPromptBuilder Build(bool withDir = false, params (string key, string value)[] config)
    {
        var settings = config.ToDictionary(c => c.key, c => (string?)c.value);
        if (withDir)
            settings[FilePromptOverrides.DirectoryKey] = _dir;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var overrides = new FilePromptOverrides(configuration, NullLogger<FilePromptOverrides>.Instance);
        return new SystemPromptBuilder(_inventory, NullLogger<SystemPromptBuilder>.Instance, configuration, overrides);
    }

    private void WriteSegment(string fileName, string text)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, fileName), text);
    }

    [Fact]
    public async Task NoConfig_UsesLibDefaultPreambleAndDeniedText()
    {
        var prompt = await Build().BuildAsync(canPerformActions: false);

        prompt.Text.Should().StartWith(KgsmAssistantPrompts.Preamble);
        prompt.Text.Should().Contain(KgsmAssistantPrompts.ActionsDenied);
        prompt.Text.Should().NotContain(KgsmAssistantPrompts.ActionsAllowed);
    }

    [Fact]
    public async Task NoConfig_AuthorizedCaller_UsesLibDefaultAllowedText()
    {
        var prompt = await Build().BuildAsync(canPerformActions: true);

        prompt.Text.Should().Contain(KgsmAssistantPrompts.ActionsAllowed);
        prompt.Text.Should().NotContain(KgsmAssistantPrompts.ActionsDenied);
    }

    [Fact]
    public async Task AutoExecuteTurn_UsesAutoText_NotProposeOnlyAllowedText()
    {
        var prompt = await Build().BuildAsync(canPerformActions: true, autoExecute: true);

        prompt.Text.Should().Contain(KgsmAssistantPrompts.ActionsAuto);
        prompt.Text.Should().NotContain(KgsmAssistantPrompts.ActionsAllowed);
        prompt.Text.Should().NotContain(KgsmAssistantPrompts.ActionsDenied);
    }

    [Fact]
    public async Task AutoExecute_IgnoredWhenNotAuthorized_StaysDenied()
    {
        // autoExecute can never widen authority: with canPerformActions=false it's still read-only.
        var prompt = await Build().BuildAsync(canPerformActions: false, autoExecute: true);

        prompt.Text.Should().Contain(KgsmAssistantPrompts.ActionsDenied);
        prompt.Text.Should().NotContain(KgsmAssistantPrompts.ActionsAuto);
    }

    [Fact]
    public async Task ConfigOverride_TakesPrecedenceOverLibDefault()
    {
        var prompt = await Build(config: new[]
        {
            ("Llm:Preamble", "CUSTOM PREAMBLE"),
            ("Llm:ActionsDenied", "CUSTOM DENIED"),
        }).BuildAsync(canPerformActions: false);

        prompt.Text.Should().StartWith("CUSTOM PREAMBLE");
        prompt.Text.Should().Contain("CUSTOM DENIED");
        prompt.Text.Should().NotContain(KgsmAssistantPrompts.Preamble);
    }

    [Fact]
    public async Task FileOverride_BeatsConfigAndConstant()
    {
        WriteSegment("preamble.md", "FILE PREAMBLE");
        var prompt = await Build(withDir: true, config: new[] { ("Llm:Preamble", "CONFIG PREAMBLE") })
            .BuildAsync(canPerformActions: false);

        prompt.Text.Should().StartWith("FILE PREAMBLE");
        prompt.Text.Should().NotContain("CONFIG PREAMBLE");
    }

    [Fact]
    public async Task BlankFile_FallsBackToConfigThenConstant()   // mid-save safety
    {
        WriteSegment("preamble.md", "   \n");   // whitespace-only ⇒ treated as absent
        var prompt = await Build(withDir: true, config: new[] { ("Llm:Preamble", "CONFIG PREAMBLE") })
            .BuildAsync(canPerformActions: false);

        prompt.Text.Should().StartWith("CONFIG PREAMBLE");
    }

    [Fact]
    public async Task EditingAFile_AppliesOnTheNextBuild_SameInstance()   // hot reload, no restart
    {
        WriteSegment("preamble.md", "VERSION ONE");
        var builder = Build(withDir: true);

        var first = await builder.BuildAsync(canPerformActions: false);
        first.Text.Should().StartWith("VERSION ONE");

        // Edit the file; the SAME builder must pick it up on the next turn (it re-reads per build).
        WriteSegment("preamble.md", "VERSION TWO");
        var second = await builder.BuildAsync(canPerformActions: false);

        second.Text.Should().StartWith("VERSION TWO");
        second.TemplateHash.Should().NotBe(first.TemplateHash);
    }

    [Fact]
    public async Task TemplateHash_MovesOnPersonaEdit_NotOnInventoryChange()
    {
        var baseline = (await Build().BuildAsync(canPerformActions: false)).TemplateHash;

        // Same persona, different live inventory → SAME template hash (lists are excluded).
        _inventory.GetInstancesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<string, string>>(
                new Dictionary<string, string> { ["terraria"] = "Terraria" }));
        var afterInstall = (await Build().BuildAsync(canPerformActions: false)).TemplateHash;
        afterInstall.Should().Be(baseline);

        // Edited persona → DIFFERENT hash.
        var edited = (await Build(config: new[] { ("Llm:Preamble", "REWORDED") })
            .BuildAsync(canPerformActions: false)).TemplateHash;
        edited.Should().NotBe(baseline);
    }

    [Fact]
    public async Task DefaultStyle_CarriesNoSpokenDeliverySegment()
    {
        var prompt = await Build().BuildAsync(canPerformActions: false);
        prompt.Text.Should().NotContain("READ ALOUD");
    }

    [Fact]
    public async Task VoiceStyle_AppendsTheSpokenSegmentLast()
    {
        // Last is the point of it: it has to survive the whole persona and the injected catalog, and be
        // the thing the model reads immediately before it answers.
        _inventory.GetInstancesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<string, string>>(
                new Dictionary<string, string> { ["terraria"] = "Terraria" }));

        var prompt = await Build().BuildAsync(canPerformActions: false, style: ReplyStyle.Voice);

        prompt.Text.Should().Contain("READ ALOUD");
        prompt.Text.TrimEnd().Should().EndWith(KgsmAssistantPrompts.Voice);
        prompt.Text.IndexOf("READ ALOUD", StringComparison.Ordinal)
            .Should().BeGreaterThan(prompt.Text.IndexOf("terraria", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VoiceStyle_IsAnEditableSegmentLikeTheOthers()
    {
        var prompt = await Build(withDir: true).BuildAsync(canPerformActions: false, style: ReplyStyle.Voice);
        prompt.Text.Should().Contain("READ ALOUD");

        WriteSegment("voice.md", "SAY IT SHORT");
        var overridden = await Build(withDir: true).BuildAsync(canPerformActions: false, style: ReplyStyle.Voice);

        overridden.Text.TrimEnd().Should().EndWith("SAY IT SHORT");
        overridden.Text.Should().NotContain("READ ALOUD");
    }

    [Fact]
    public async Task TemplateHash_SeparatesAStyledTurnFromAPlainOne()
    {
        // A turn answered in a different shape was built from a different prompt, and the recorded hash
        // is what a later transcript read buckets by.
        var plain = (await Build().BuildAsync(canPerformActions: false)).TemplateHash;
        var spoken = (await Build().BuildAsync(canPerformActions: false, style: ReplyStyle.Voice)).TemplateHash;

        spoken.Should().NotBe(plain);
    }

    [Fact]
    public async Task VoiceStyle_LeavesTheAuthorizationStanceAlone()
    {
        // Presentation only: the same refusal text a denied caller gets on a screen is what they get
        // through a speaker. A style must never be a route to a different stance.
        var denied = await Build().BuildAsync(canPerformActions: false, style: ReplyStyle.Voice);
        var allowed = await Build().BuildAsync(canPerformActions: true, style: ReplyStyle.Voice);

        denied.Text.Should().Contain(KgsmAssistantPrompts.ActionsDenied);
        allowed.Text.Should().Contain(KgsmAssistantPrompts.ActionsAllowed);
    }
}
