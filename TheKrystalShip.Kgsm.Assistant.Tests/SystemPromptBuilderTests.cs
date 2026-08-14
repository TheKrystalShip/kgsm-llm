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
        _inventory.GetBlueprintCatalogAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<BlueprintSummary>>(Array.Empty<BlueprintSummary>()));
    }

    /// <summary>The catalog as the engine hands it over: an identifier and the game's real name.</summary>
    private void Catalog(params (string name, string? display)[] blueprints) =>
        _inventory.GetBlueprintCatalogAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<BlueprintSummary>>(
                blueprints.Select(b => new BlueprintSummary(b.name, b.display)).ToArray()));

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

    // --- the injected lists name games the way a person does ---
    // This list is read out verbatim on a spoken surface, and the model answers "what can I install?"
    // straight from it, so the identifier never reaches a person's ears.

    [Fact]
    public async Task InstallableGames_AreListedByDisplayName()
    {
        Catalog(("7dtd", "7 Days to Die"), ("projectzomboid", "Project Zomboid"));

        var prompt = await Build().BuildAsync(canPerformActions: false);

        prompt.Text.Should().Contain("7 Days to Die").And.Contain("Project Zomboid");
        prompt.Text.Should().NotContain("7dtd").And.NotContain("projectzomboid");
    }

    [Fact]
    public async Task ABlueprintWithNoDisplayName_KeepsItsIdentifier()
    {
        Catalog(("homebrew", null));

        var prompt = await Build().BuildAsync(canPerformActions: false);

        prompt.Text.Should().Contain("homebrew");
    }

    [Fact]
    public async Task AnInstance_NamesItsGame_WithoutTheBlueprintFileStem()
    {
        // An instance carries its blueprint's file stem ("projectzomboid.bp"), which is neither a word
        // anybody says nor the key the catalog is under.
        Catalog(("projectzomboid", "Project Zomboid"));
        _inventory.GetInstancesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<string, string>>(
                new Dictionary<string, string> { ["pz-main"] = "projectzomboid.bp" }));

        var prompt = await Build().BuildAsync(canPerformActions: false);

        prompt.Text.Should().Contain("pz-main (game: Project Zomboid)");
        prompt.Text.Should().NotContain(".bp");
    }

    [Fact]
    public async Task AnInstance_WhoseGameTheCatalogDoesNotKnow_KeepsTheEnginesWord()
    {
        _inventory.GetInstancesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<string, string>>(
                new Dictionary<string, string> { ["orphan"] = "retiredgame.bp" }));

        var prompt = await Build().BuildAsync(canPerformActions: false);

        prompt.Text.Should().Contain("orphan (game: retiredgame.bp)");
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
