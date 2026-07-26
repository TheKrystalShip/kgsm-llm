using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Blueprints;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// The agentic research sub-loop: the model drives search + fetch_url to gather pages, then the
/// synthesizer extracts from what was fetched. Covers the load-bearing behaviours: a search→fetch→done
/// trajectory feeds the fetched page into synthesis and returns its result; and any dead-end (model
/// unavailable, or a run that gathered nothing) degrades to the fixed one-query fallback pass rather than
/// returning empty.
/// </summary>
public sealed class AgenticBlueprintResearchTests
{
    private readonly ILlmClient _llm = Substitute.For<ILlmClient>();
    private readonly IWebSearch _search = Substitute.For<IWebSearch>();
    private readonly IWebFetch _fetch = Substitute.For<IWebFetch>();
    private readonly IBlueprintSynthesizer _synth = Substitute.For<IBlueprintSynthesizer>();

    private AgenticBlueprintResearch Sut(BlueprintResearchAggregator fixedFallback) =>
        new(_llm, _search, _fetch, _synth, fixedFallback, NullLogger<AgenticBlueprintResearch>.Instance);

    // A fixed-pass fallback wired to distinct fakes so a delegation to it is observable.
    private BlueprintResearchAggregator FixedReturning(string narrative)
    {
        var fs = Substitute.For<IWebSearch>();
        var ff = Substitute.For<IWebFetch>();
        var fsy = Substitute.For<IBlueprintSynthesizer>();
        fs.SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<WebSearchHit>>([new WebSearchHit("t", "https://fixed.example/x", "c", 0.9)]));
        ff.FetchAsync("https://fixed.example/x", Arg.Any<CancellationToken>())
            .Returns(Result.Success(new WebFetchResult("https://fixed.example/x", 200, "text/html", null, "Linux dedicated server. ./run.sh", false)));
        fsy.SynthesizeAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns(new BlueprintResearchFindings(BlueprintFeasibility.Feasible, "X", [], ["https://fixed.example/x"], narrative));
        return new BlueprintResearchAggregator(fs, ff, fsy, NullLogger<BlueprintResearchAggregator>.Instance);
    }

    [Fact]
    public async Task ModelSearchesThenFetches_ThenSynthesizesTheFetchedPage()
    {
        var searchCall = new LlmResponse(null, [new LlmToolCall(new Tool("search"), Args(("query", "necesse dedicated server linux")))]);
        var fetchCall = new LlmResponse(null, [new LlmToolCall(new Tool("fetch_url"), Args(("url", "https://necessewiki.com/Multiplayer")))]);
        var done = new LlmResponse("I found the launch script and app id.", []);
        _llm.ChatAsync(Arg.Any<IReadOnlyList<LlmMessage>>(), Arg.Any<IReadOnlyList<LlmToolDefinition>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Result<LlmResponse>.Success(searchCall), Result<LlmResponse>.Success(fetchCall), Result<LlmResponse>.Success(done));
        _search.SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<WebSearchHit>>([new WebSearchHit("Necesse wiki", "https://necessewiki.com/Multiplayer", "c", 0.9)]));
        _fetch.FetchAsync("https://necessewiki.com/Multiplayer", Arg.Any<CancellationToken>())
            .Returns(Result.Success(new WebFetchResult("https://necessewiki.com/Multiplayer", 200, "text/html", null,
                "Run StartServer-nogui.sh. steamcmd +app_update 1169370. Port 14159.", false)));
        _synth.SynthesizeAsync("Necesse", Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns(new BlueprintResearchFindings(BlueprintFeasibility.Feasible, "Necesse",
                [new BlueprintResearchField("executable_file", "StartServer-nogui.sh", "https://necessewiki.com/Multiplayer")],
                ["https://necessewiki.com/Multiplayer"], "SYNTH"));

        var findings = await Sut(FixedReturning("FIXED")).ResearchAsync("Necesse");

        findings.Narrative.Should().Be("SYNTH");
        findings.Fields.Should().ContainSingle(f => f.Name == "executable_file" && f.Value == "StartServer-nogui.sh");
        await _fetch.Received(1).FetchAsync("https://necessewiki.com/Multiplayer", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ModelUnavailable_DelegatesToTheFixedPass()
    {
        _llm.ChatAsync(Arg.Any<IReadOnlyList<LlmMessage>>(), Arg.Any<IReadOnlyList<LlmToolDefinition>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<LlmResponse>("model is down"));

        var findings = await Sut(FixedReturning("FIXED")).ResearchAsync("Necesse");

        findings.Narrative.Should().Be("FIXED");
        await _synth.DidNotReceiveWithAnyArgs().SynthesizeAsync(default!, default!, default);
    }

    [Fact]
    public async Task ModelFetchesNothing_DelegatesToTheFixedPass()
    {
        // The model just replies with no tool calls — gathered zero pages, so the fixed pass takes over.
        _llm.ChatAsync(Arg.Any<IReadOnlyList<LlmMessage>>(), Arg.Any<IReadOnlyList<LlmToolDefinition>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Result<LlmResponse>.Success(new LlmResponse("I don't know where to look.", [])));

        var findings = await Sut(FixedReturning("FIXED")).ResearchAsync("Necesse");

        findings.Narrative.Should().Be("FIXED");
    }

    [Fact]
    public async Task RepeatedFetchOfSameUrl_SpendsNoBudget_SoADifferentSourceStillGetsFetched()
    {
        // The model asks for the SAME page more times than the whole fetch budget (6), then a different
        // source. Re-fetches must be short-circuited (one request, no budget spent) so the fresh source is
        // still reached — the failure mode where a model spun on one long wiki page and never gathered a
        // second source, burning the budget on identical reads.
        var fetchCalls = new LlmToolCall[]
        {
            new(new Tool("fetch_url"), Args(("url", "https://wiki.example/Big"))),
            new(new Tool("fetch_url"), Args(("url", "https://wiki.example/Big"))),
            new(new Tool("fetch_url"), Args(("url", "https://wiki.example/Big"))),
            new(new Tool("fetch_url"), Args(("url", "https://wiki.example/Big"))),
            new(new Tool("fetch_url"), Args(("url", "https://wiki.example/Big"))),
            new(new Tool("fetch_url"), Args(("url", "https://wiki.example/Big"))),
            new(new Tool("fetch_url"), Args(("url", "https://wiki.example/Big"))),
            new(new Tool("fetch_url"), Args(("url", "https://other.example/Guide"))),
        };
        _llm.ChatAsync(Arg.Any<IReadOnlyList<LlmMessage>>(), Arg.Any<IReadOnlyList<LlmToolDefinition>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Result<LlmResponse>.Success(new LlmResponse(null, fetchCalls)),
                     Result<LlmResponse>.Success(new LlmResponse("done", [])));
        _fetch.FetchAsync("https://wiki.example/Big", Arg.Any<CancellationToken>())
            .Returns(Result.Success(new WebFetchResult("https://wiki.example/Big", 200, "text/html", null, "boilerplate", false)));
        _fetch.FetchAsync("https://other.example/Guide", Arg.Any<CancellationToken>())
            .Returns(Result.Success(new WebFetchResult("https://other.example/Guide", 200, "text/html", null, "./run.sh steamcmd +app_update 1", false)));
        _synth.SynthesizeAsync("Big", Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns(new BlueprintResearchFindings(BlueprintFeasibility.Feasible, "Big", [], [], "S"));

        await Sut(FixedReturning("FIXED")).ResearchAsync("Big");

        await _fetch.Received(1).FetchAsync("https://wiki.example/Big", Arg.Any<CancellationToken>());
        await _fetch.Received(1).FetchAsync("https://other.example/Guide", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LongPage_KeepsTheDeepLaunchSection_NotJustTheBoilerplateHead()
    {
        // A page longer than the per-page cap whose launch instructions sit far past a nav/TOC block: a
        // plain head cut throws them away (extraction then sees only boilerplate). The keyword-centered
        // slice must carry the launch section into what synthesis reads.
        var page = new string('x', 9000) + " Starting the server on Linux: run ./FactoryServer.sh -log";
        var fetchCall = new LlmResponse(null, [new LlmToolCall(new Tool("fetch_url"), Args(("url", "https://sat.example/Dedicated")))]);
        _llm.ChatAsync(Arg.Any<IReadOnlyList<LlmMessage>>(), Arg.Any<IReadOnlyList<LlmToolDefinition>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Result<LlmResponse>.Success(fetchCall), Result<LlmResponse>.Success(new LlmResponse("done", [])));
        _fetch.FetchAsync("https://sat.example/Dedicated", Arg.Any<CancellationToken>())
            .Returns(Result.Success(new WebFetchResult("https://sat.example/Dedicated", 200, "text/html", null, page, false)));
        _synth.SynthesizeAsync("Satisfactory", Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns(new BlueprintResearchFindings(BlueprintFeasibility.Feasible, "Satisfactory", [], [], "S"));

        await Sut(FixedReturning("FIXED")).ResearchAsync("Satisfactory");

        await _synth.Received(1).SynthesizeAsync("Satisfactory",
            Arg.Is<IReadOnlyList<(string, string)>>(pages => pages.Any(p => p.Item2.Contains("FactoryServer.sh"))),
            Arg.Any<CancellationToken>());
    }

    private static IReadOnlyDictionary<string, string?> Args(params (string Key, string? Value)[] kv) =>
        kv.ToDictionary(x => x.Key, x => x.Value);
}
