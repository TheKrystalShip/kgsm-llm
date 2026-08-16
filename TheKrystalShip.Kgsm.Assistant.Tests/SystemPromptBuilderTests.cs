using FluentAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Ports;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// The prompt text is FILES, installed beside the service. These verify the precedence — editable
/// file > inline Llm:* config, with no third fallback — and that the recorded
/// <see cref="BuiltPrompt.TemplateHash"/> tracks the EDITABLE template (it moves when the persona
/// changes, not when the injected live lists do).
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

        // The state a deploy leaves behind. Seeded once so a test that writes a segment afterwards is
        // writing OVER the shipped text, which is what an operator's edit actually is.
        ShippedText.SeedInto(_dir);
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

    /// <summary>
    /// A builder over a directory seeded with the SHIPPED text, which is the state a deploy leaves
    /// behind. <paramref name="seed"/> false leaves the directory empty, for the missing-file cases.
    /// </summary>
    private SystemPromptBuilder Build(bool seed = true, params (string key, string value)[] config)
    {
        var settings = config.ToDictionary(c => c.key, c => (string?)c.value);
        settings[FilePromptOverrides.DirectoryKey] = _dir;
        if (!seed && Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);

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
    public async Task ShippedFiles_ProvideThePreambleAndDeniedText()
    {
        var prompt = await Build().BuildAsync(canPerformActions: false);

        prompt.Text.Should().StartWith(ShippedText.Segment("preamble.md"));
        prompt.Text.Should().Contain(ShippedText.Segment("actions-denied.md"));
        prompt.Text.Should().NotContain(ShippedText.Segment("actions-allowed.md"));
    }

    [Fact]
    public async Task ShippedFiles_AuthorizedCaller_GetTheAllowedText()
    {
        var prompt = await Build().BuildAsync(canPerformActions: true);

        prompt.Text.Should().Contain(ShippedText.Segment("actions-allowed.md"));
        prompt.Text.Should().NotContain(ShippedText.Segment("actions-denied.md"));
    }

    [Fact]
    public async Task AutoExecuteTurn_UsesAutoText_NotProposeOnlyAllowedText()
    {
        var prompt = await Build().BuildAsync(canPerformActions: true, autoExecute: true);

        prompt.Text.Should().Contain(ShippedText.Segment("actions-auto.md"));
        prompt.Text.Should().NotContain(ShippedText.Segment("actions-allowed.md"));
        prompt.Text.Should().NotContain(ShippedText.Segment("actions-denied.md"));
    }

    [Fact]
    public async Task AutoExecute_IgnoredWhenNotAuthorized_StaysDenied()
    {
        // autoExecute can never widen authority: with canPerformActions=false it's still read-only.
        var prompt = await Build().BuildAsync(canPerformActions: false, autoExecute: true);

        prompt.Text.Should().Contain(ShippedText.Segment("actions-denied.md"));
        prompt.Text.Should().NotContain(ShippedText.Segment("actions-auto.md"));
    }

    [Fact]
    public async Task ConfigOverride_IsOutrankedByTheInstalledFile()
    {
        // The file is what the host runs on. A config key set beside an installed file is the
        // less specific of the two, and silently winning over the file an operator just edited is
        // exactly the confusion this precedence exists to avoid.
        var prompt = await Build(config: new[]
        {
            ("Llm:Preamble", "CUSTOM PREAMBLE"),
            ("Llm:ActionsDenied", "CUSTOM DENIED"),
        }).BuildAsync(canPerformActions: false);

        prompt.Text.Should().StartWith(ShippedText.Segment("preamble.md"));
        prompt.Text.Should().NotContain("CUSTOM PREAMBLE");
    }

    [Fact]
    public async Task FileOverride_BeatsConfigAndConstant()
    {
        WriteSegment("preamble.md", "FILE PREAMBLE");
        var prompt = await Build(seed: true, config: new[] { ("Llm:Preamble", "CONFIG PREAMBLE") })
            .BuildAsync(canPerformActions: false);

        prompt.Text.Should().StartWith("FILE PREAMBLE");
        prompt.Text.Should().NotContain("CONFIG PREAMBLE");
    }

    [Fact]
    public async Task BlankFile_FallsBackToConfigThenConstant()   // mid-save safety
    {
        WriteSegment("preamble.md", "   \n");   // whitespace-only ⇒ treated as absent
        var prompt = await Build(seed: true, config: new[] { ("Llm:Preamble", "CONFIG PREAMBLE") })
            .BuildAsync(canPerformActions: false);

        prompt.Text.Should().StartWith("CONFIG PREAMBLE");
    }

    [Fact]
    public async Task EditingAFile_AppliesOnTheNextBuild_SameInstance()   // hot reload, no restart
    {
        WriteSegment("preamble.md", "VERSION ONE");
        var builder = Build(seed: true);

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

        // Edited persona → DIFFERENT hash. Edited where an operator actually edits it: the file.
        WriteSegment("preamble.md", "REWORDED");
        var edited = (await Build().BuildAsync(canPerformActions: false)).TemplateHash;
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
        prompt.Text.TrimEnd().Should().EndWith(ShippedText.Segment("voice.md"));
        prompt.Text.IndexOf("READ ALOUD", StringComparison.Ordinal)
            .Should().BeGreaterThan(prompt.Text.IndexOf("terraria", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VoiceStyle_IsAnEditableSegmentLikeTheOthers()
    {
        var prompt = await Build(seed: true).BuildAsync(canPerformActions: false, style: ReplyStyle.Voice);
        prompt.Text.Should().Contain("READ ALOUD");

        WriteSegment("voice.md", "SAY IT SHORT");
        var overridden = await Build(seed: true).BuildAsync(canPerformActions: false, style: ReplyStyle.Voice);

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

        denied.Text.Should().Contain(ShippedText.Segment("actions-denied.md"));
        allowed.Text.Should().Contain(ShippedText.Segment("actions-allowed.md"));
    }

    [Fact]
    public async Task MissingSegmentFile_IsRefused_RatherThanAnsweredFromNothing()
    {
        // No fallback exists any more: a segment that is not on disk and not configured is a fault.
        // Silently answering without the persona would not look like a failure — it would look like
        // the assistant changing its mind about what it is.
        var act = async () => await Build(seed: false).BuildAsync(canPerformActions: false);

        await act.Should().ThrowAsync<AssistantTextUnavailableException>()
            .WithMessage("*preamble.md*");
    }

    [Fact]
    public async Task InlineConfig_StillAnswersWhenTheFileIsAbsent()
    {
        var prompt = await Build(seed: false,
            ("Llm:Preamble", "CONFIGURED PREAMBLE"),
            ("Llm:ActionsDenied", "CONFIGURED DENIED")).BuildAsync(canPerformActions: false);

        prompt.Text.Should().StartWith("CONFIGURED PREAMBLE");
    }
}
