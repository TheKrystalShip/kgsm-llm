using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Blueprints;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// The blueprint-research composer (plan step 2): it goes DIRECTLY to <see cref="IWebSearch"/> (fetchable
/// third-party pages), fetches them via <see cref="IWebFetch"/>, and extracts sourced fields — proven
/// black-box here through the public port so the internal extractor's heuristics are covered too. The
/// load-bearing behaviours: web search that returns nothing (or is unconfigured) is an honest
/// "nothing to research from" that never fetches; and a documented headless launch command + a real
/// server-ready log line become the <c>executable_arguments</c> / <c>startup_success_regex</c> fields
/// that a non-interactive boot depends on — every field sourced, never fabricated.
/// </summary>
public sealed class BlueprintResearchAggregatorTests
{
    private readonly IWebSearch _search = Substitute.For<IWebSearch>();
    private readonly IWebFetch _fetch = Substitute.For<IWebFetch>();
    // Default: synthesis returns null (unconfigured/inconclusive) so these tests exercise the deterministic
    // regex-extraction fallback. The synthesis-first path has its own test that stubs a result.
    private readonly IBlueprintSynthesizer _synth = Substitute.For<IBlueprintSynthesizer>();

    private BlueprintResearchAggregator Sut() =>
        new(_search, _fetch, _synth, NullLogger<BlueprintResearchAggregator>.Instance);

    private void SearchReturns(params string[] urls) =>
        _search.SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<WebSearchHit>>(
                urls.Select(u => new WebSearchHit("t", u, "c", 0.9)).ToList()));

    private void PageReturns(string url, string text) =>
        _fetch.FetchAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result.Success(new WebFetchResult(url, 200, "text/html", null, text, false)));

    [Fact]
    public async Task UnconfiguredWebSearch_IsInconclusive_AndNeverFetches()
    {
        _search.SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<IReadOnlyList<WebSearchHit>>("web search is not configured on this host"));

        var findings = await Sut().ResearchAsync("Terraria");

        findings.Feasibility.Should().Be(BlueprintFeasibility.Inconclusive);
        await _fetch.DidNotReceiveWithAnyArgs().FetchAsync(default!, default);
    }

    [Fact]
    public async Task EmptyWebResults_IsInconclusive_AndNeverFetches()
    {
        _search.SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<WebSearchHit>>([]));

        var findings = await Sut().ResearchAsync("Terraria");

        findings.Feasibility.Should().Be(BlueprintFeasibility.Inconclusive);
        await _fetch.DidNotReceiveWithAnyArgs().FetchAsync(default!, default);
    }

    [Fact]
    public async Task DocumentedLaunchCommand_SourcesExecutableArguments_ForNonInteractiveBoot()
    {
        SearchReturns("https://guide.example/terraria");
        PageReturns("https://guide.example/terraria",
            "Terraria has a Linux dedicated server. Start it with steamcmd is not needed; run:\n" +
            "./TerrariaServer.bin.x86_64 -world world.wld -autocreate 3 -worldname World\n" +
            "The server opens on port 7777. Server started once you see the console.");

        var findings = await Sut().ResearchAsync("Terraria");

        findings.Feasibility.Should().Be(BlueprintFeasibility.Feasible);
        Field(findings, "executable_file").Should().Be("TerrariaServer.bin.x86_64");
        Field(findings, "executable_arguments").Should().Be("-world world.wld -autocreate 3 -worldname World");
        Field(findings, "ports").Should().Be("7777");
        Field(findings, "startup_success_regex").Should().Be("Server started");
    }

    [Fact]
    public async Task ExecutableArguments_AreTrimmedAtProseBoundary_WhenHtmlFlatteningBleedsAsentenceIn()
    {
        // HTML→text collapsed the doc's line breaks, so the launch command runs straight into the next
        // sentence — the exact shape a live Terraria page produced. Only the argument tail should survive.
        SearchReturns("https://guide.example/terraria");
        PageReturns("https://guide.example/terraria",
            "Terraria ships a Linux dedicated server. Run it like this:\n" +
            "./TerrariaServer.bin.x86_64 -config serverconfig.txt . The most important keys are in the table below.");

        var findings = await Sut().ResearchAsync("Terraria");

        Field(findings, "executable_arguments").Should().Be("-config serverconfig.txt");
    }

    [Fact]
    public async Task LauncherNamedWithoutInvoker_IsStillSourcedAsExecutableFile()
    {
        // Necesse's launch script is referenced by name, not as "./…" — the "start/server/launch/run"
        // name gate lets it through while an unrelated steamcmd.sh in the same doc is ignored.
        SearchReturns("https://guide.example/necesse");
        PageReturns("https://guide.example/necesse",
            "Necesse has a Linux dedicated server. First run steamcmd.sh to download it, then launch the\n" +
            "server with StartServer-nogui.sh. It opens on port 14159.");

        var findings = await Sut().ResearchAsync("Necesse");

        Field(findings, "executable_file").Should().Be("StartServer-nogui.sh");
    }

    [Fact]
    public async Task SteamcmdAppUpdate_SourcesTheDedicatedServerAppId()
    {
        SearchReturns("https://guide.example/necesse");
        PageReturns("https://guide.example/necesse",
            "Necesse has a Linux dedicated server. Install it with SteamCMD:\n" +
            "steamcmd +login anonymous +app_update 1169370 validate +quit\n" +
            "It listens on port 14159.");

        var findings = await Sut().ResearchAsync("Necesse");

        Field(findings, "steam_app_id").Should().Be("1169370", "the +app_update id is the dedicated-server app id");
    }

    [Fact]
    public async Task BareStoreUrl_DoesNotMistakeTheClientAppIdForTheServer()
    {
        // A store/steamdb URL carries the CLIENT app id (Necesse client = 1169040). Installing against a
        // client id is wrong, so with no server-download context the field stays unsourced (→ schema 0).
        SearchReturns("https://guide.example/necesse");
        PageReturns("https://guide.example/necesse",
            "Necesse ships a Linux dedicated server. See the store page: " +
            "https://store.steampowered.com/app/1169040 for details. It listens on port 14159.");

        var findings = await Sut().ResearchAsync("Necesse");

        findings.Fields.Should().NotContain(f => f.Name == "steam_app_id");
    }

    [Fact]
    public async Task SteamAccountRequiredPhrase_NoLongerStops_OwnershipDecidedEmpirically()
    {
        // The deterministic extractor no longer gates on ownership phrasing — whether the server files need
        // an owning account is measured by the anonymous test-install downstream, not inferred from a page.
        SearchReturns("https://guide.example/starbound");
        PageReturns("https://guide.example/starbound",
            "Starbound has a Linux dedicated server. You need a Steam account that owns the game to download the server files.\n" +
            "steamcmd +login anonymous +app_update 211820 validate +quit\nThen run ./starbound_server. Port 21025.");

        var findings = await Sut().ResearchAsync("Starbound");

        findings.Feasibility.Should().NotBe(BlueprintFeasibility.RequiresSteamAccount,
            "ownership is no longer inferred from page phrasing — the test-install measures it");
    }

    [Fact]
    public async Task GenericMustOwnClientPhrase_DoesNotFalselyStop_WhenServerDownloadsAnonymously()
    {
        // A page can say "you must own the game to play" (about the CLIENT) while the SERVER downloads
        // anonymously under its own app id. The deterministic fallback can't compare app ids, so its
        // phrase gate must be narrow enough not to decline a game that would actually install.
        SearchReturns("https://guide.example/romestead");
        PageReturns("https://guide.example/romestead",
            "Romestead has a Linux dedicated server. You must own the game to play. Install the server:\n" +
            "steamcmd +login anonymous +app_update 4763510 validate +quit\n" +
            "Then run ./Server.sh. It listens on port 8050.");

        var findings = await Sut().ResearchAsync("Romestead");

        findings.Feasibility.Should().NotBe(BlueprintFeasibility.RequiresSteamAccount,
            "a client-ownership phrase must not gate a server that downloads anonymously");
    }

    [Fact]
    public async Task ChmodLineOnly_DoesNotFabricateExecutableArguments()
    {
        SearchReturns("https://guide.example/x");
        PageReturns("https://guide.example/x",
            "This game ships a Linux dedicated server. Make it runnable:\n" +
            "chmod +x ./ServerApp.x86_64\n" +
            "Then follow the interactive prompts.");

        var findings = await Sut().ResearchAsync("SomeGame");

        findings.Feasibility.Should().Be(BlueprintFeasibility.Feasible);
        Field(findings, "executable_file").Should().Be("ServerApp.x86_64");
        findings.Fields.Should().NotContain(f => f.Name == "executable_arguments");
        findings.Fields.Should().NotContain(f => f.Name == "startup_success_regex");
    }

    [Fact]
    public async Task SynthesisResult_IsUsedDirectly_AndSuppressesRegexFallback()
    {
        // The page text would make the regex extractor pick the Docker entrypoint "entry.sh"; synthesis
        // (reading in context) returns the native launcher instead, and its result is used verbatim.
        SearchReturns("https://guide.example/necesse");
        PageReturns("https://guide.example/necesse",
            "Necesse Linux dedicated server. Docker users: ./entry.sh. Native: bash StartServer-nogui.sh.");
        _synth.SynthesizeAsync("Necesse", Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns(new BlueprintResearchFindings(
                BlueprintFeasibility.Feasible, "Necesse",
                [new BlueprintResearchField("executable_file", "StartServer-nogui.sh", "https://guide.example/necesse")],
                ["https://guide.example/necesse"], "synthesized"));

        var findings = await Sut().ResearchAsync("Necesse");

        Field(findings, "executable_file").Should().Be("StartServer-nogui.sh");
        findings.Narrative.Should().Be("synthesized");
    }

    [Fact]
    public async Task SynthesisFeasibilityVerdict_IsTrusted_WithoutRegexFallback()
    {
        SearchReturns("https://guide.example/x");
        PageReturns("https://guide.example/x", "This game has a Linux dedicated server: ./run.sh.");
        _synth.SynthesizeAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns(new BlueprintResearchFindings(
                BlueprintFeasibility.NotSelfHostable, null, [], ["https://guide.example/x"], "cannot self-host"));

        var findings = await Sut().ResearchAsync("SomeGame");

        findings.Feasibility.Should().Be(BlueprintFeasibility.NotSelfHostable);
    }

    private static string? Field(BlueprintResearchFindings findings, string name) =>
        findings.Fields.FirstOrDefault(f => f.Name == name)?.Value;
}
