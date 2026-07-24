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

    private BlueprintResearchAggregator Sut() =>
        new(_search, _fetch, NullLogger<BlueprintResearchAggregator>.Instance);

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

    private static string? Field(BlueprintResearchFindings findings, string name) =>
        findings.Fields.FirstOrDefault(f => f.Name == name)?.Value;
}
