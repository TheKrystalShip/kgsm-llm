using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Envelope;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Kgsm.Assistant.Search;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// The deterministic composer: local indexed docs first, public web fallback. Covers the whole
/// ladder — strong local answers without touching the web; weak/empty/disabled local falls back;
/// a weak local hit beats an empty web; the context cap keeps the strongest chunk; and a web
/// FAILURE is reported as "couldn't search", never as "nothing exists" (the measured-or-unknown rule).
/// </summary>
public class SearchAggregatorTests
{
    private readonly IRetrieval _retrieval = Substitute.For<IRetrieval>();
    private readonly IWebSearch _web = Substitute.For<IWebSearch>();

    private SearchAggregator Create(SearchOptions? opts = null) =>
        new(_retrieval, _web, Options.Create(opts ?? new SearchOptions()), NullLogger<SearchAggregator>.Instance);

    private void LocalReturns(params (string text, double score)[] hits) =>
        _retrieval.RetrieveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<RetrievedChunk>>(
                hits.Select(h => new RetrievedChunk("docs/x.md", "X > Y", h.text, h.score)).ToArray()));

    private void LocalFails(string error = "local retrieval is not enabled on this host") =>
        _retrieval.RetrieveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<IReadOnlyList<RetrievedChunk>>(error));

    private void WebReturns(params WebSearchHit[] hits) =>
        _web.SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<WebSearchHit>>(hits));

    private void WebFails(string error) =>
        _web.SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<IReadOnlyList<WebSearchHit>>(error));

    [Fact]
    public async Task A_strong_local_hit_answers_from_docs_without_touching_the_web()
    {
        LocalReturns(("KGSM manages servers via a stateless CLI.", 0.80));

        var result = await Create(new SearchOptions { LocalMinScore = 0.35 }).SearchAsync("what is kgsm");

        result.Summary.Should().Contain("indexed docs")
            .And.Contain("KGSM manages servers")
            .And.Contain("docs/x.md");
        await _web.DidNotReceive().SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_weak_local_hit_falls_back_to_the_web_when_the_web_has_results()
    {
        LocalReturns(("vaguely related", 0.10));
        WebReturns(new WebSearchHit("Terraria 1.4.5", "https://terraria.org", "the latest release", 0.9));

        var result = await Create(new SearchOptions { LocalMinScore = 0.35 }).SearchAsync("terraria version");

        result.Summary.Should().Contain("Web results").And.Contain("terraria.org").And.Contain("out of date");
        await _web.Received(1).SearchAsync("terraria version", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_disabled_or_unbuilt_local_index_falls_back_to_the_web()
    {
        LocalFails();
        WebReturns(new WebSearchHit("Title", "https://example.org", "content", 0.9));

        var result = await Create().SearchAsync("q");

        result.Summary.Should().Contain("Web results").And.Contain("https://example.org");
    }

    [Fact]
    public async Task A_weak_local_hit_beats_nothing_when_the_web_is_empty()
    {
        LocalReturns(("the closest passage we have", 0.10));
        WebReturns(); // success, no hits

        var result = await Create(new SearchOptions { LocalMinScore = 0.35 }).SearchAsync("q");

        result.Summary.Should().Contain("closest passages").And.Contain("the closest passage we have");
    }

    [Fact]
    public async Task A_web_failure_with_no_local_is_reported_as_couldnt_search_not_nothing_found()
    {
        LocalFails();
        WebFails("the daily web-search limit has been reached");

        var result = await Create().SearchAsync("obscure thing");

        result.Summary.Should().Contain("Couldn't search")
            .And.Contain("daily web-search limit")
            .And.Contain("do not retry");
        result.Summary.Should().NotContain("No results", "a web failure must not be narrated as the thing not existing");
    }

    [Fact]
    public async Task A_genuine_empty_on_both_sources_says_nothing_was_found()
    {
        LocalReturns(); // success, no hits
        WebReturns();   // success, no hits

        var result = await Create().SearchAsync("nonexistent");

        result.Summary.Should().Contain("No results").And.Contain("indexed docs or on the web");
    }

    [Fact]
    public async Task The_context_cap_keeps_at_least_the_strongest_chunk_and_notes_omissions()
    {
        var big = new string('a', 500);
        LocalReturns(("strong " + big, 0.90), ("second " + big, 0.85), ("third " + big, 0.80));

        var result = await Create(new SearchOptions { LocalMinScore = 0.35, MaxContextChars = 600 }).SearchAsync("q");

        result.Summary.Should().Contain("strong");           // the strongest chunk is always included
        result.Summary.Should().Contain("omitted to fit");   // the rest are dropped to fit the window
        result.Summary.Should().NotContain("second").And.NotContain("third");
    }

    // --- the SearchData card (the surface half of the envelope) ---

    [Fact]
    public async Task A_strong_local_hit_carries_a_LocalStrong_card_with_cited_passages()
    {
        LocalReturns(("KGSM manages servers via a stateless CLI.", 0.80));

        var result = await Create(new SearchOptions { LocalMinScore = 0.35 }).SearchAsync("what is kgsm");

        result.Tool.Should().Be(ResultCardKinds.Search);
        result.Confidence.Should().Be(Confidence.Confirmed);   // measured — the operator's own docs
        result.Subject.Should().Be(new ResultRef(ResourceKind.Search, "what is kgsm"));
        result.Data.State.Should().Be(SearchState.LocalStrong);
        result.Data.Passages.Should().ContainSingle();
        var p = result.Data.Passages[0];
        p.Provenance.Should().Be(SearchProvenance.Local);
        p.Source.Should().Be("docs/x.md");
        p.Title.Should().Be("X > Y");
        p.Text.Should().Contain("KGSM manages servers");
        p.Score.Should().Be(0.80);
    }

    [Fact]
    public async Task A_web_answer_carries_a_Web_card_with_the_url_and_likely_confidence()
    {
        LocalReturns(("vaguely related", 0.10));
        WebReturns(new WebSearchHit("Terraria 1.4.5", "https://terraria.org", "the latest release", 0.9));

        var result = await Create(new SearchOptions { LocalMinScore = 0.35 }).SearchAsync("terraria version");

        result.Confidence.Should().Be(Confidence.Likely);       // external, possibly stale
        result.Data.State.Should().Be(SearchState.Web);
        result.Data.Passages.Should().ContainSingle();
        result.Data.Passages[0].Provenance.Should().Be(SearchProvenance.Web);
        result.Data.Passages[0].Source.Should().Be("https://terraria.org");
        result.Data.Passages[0].Title.Should().Be("Terraria 1.4.5");
    }

    [Fact]
    public async Task An_empty_or_failed_search_carries_no_passages_so_no_card_is_surfaced()
    {
        LocalReturns(); // success, no hits
        WebReturns();   // success, no hits

        var empty = await Create().SearchAsync("nonexistent");
        empty.Data.State.Should().Be(SearchState.Empty);
        empty.Data.Passages.Should().BeEmpty();

        LocalFails();
        WebFails("the daily web-search limit has been reached");
        var failed = await Create().SearchAsync("obscure");
        failed.Data.State.Should().Be(SearchState.SearchFailed);
        failed.Data.Passages.Should().BeEmpty();
    }

    // --- scope: where the caller said to look -------------------------------------------------

    [Fact]
    public async Task Asking_for_the_web_skips_the_local_docs_entirely()
    {
        // The measured failure this exists for: "next Valheim update date" matched fifty-four
        // Valheim guide chunks on topic alone and never reached the web, so somebody who asked to
        // check online was answered out of a local guide that says nothing about release dates.
        LocalReturns(("KGSM manages game servers.", 0.95));
        WebReturns(new WebSearchHit("Valheim patch", "https://valheim.com/news", "…", 0.9));

        var result = await Create().SearchAsync("next valheim update", SearchScope.Web);

        result.Data.State.Should().Be(SearchState.Web);
        await _retrieval.DidNotReceive().RetrieveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Asking_for_the_web_and_finding_nothing_does_not_fall_back_to_the_docs()
    {
        // A local passage the caller ruled out is not a better answer than saying nothing was found.
        LocalReturns(("KGSM manages game servers.", 0.95));
        WebReturns();

        var result = await Create().SearchAsync("next valheim update", SearchScope.Web);

        result.Data.State.Should().Be(SearchState.Empty);
        result.Data.Passages.Should().BeEmpty();
    }

    [Fact]
    public async Task Confining_a_search_to_the_docs_never_calls_the_web()
    {
        LocalReturns(("Something loosely related.", 0.10));

        var result = await Create().SearchAsync("terraria version", SearchScope.Local);

        await _web.DidNotReceive().SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        result.Data.State.Should().Be(SearchState.LocalWeak);
    }

    [Fact]
    public async Task The_default_is_unchanged()
    {
        // Everything above is opt-in. A caller that says nothing still gets docs-first.
        LocalReturns(("KGSM manages game servers.", 0.95));

        var result = await Create().SearchAsync("what is kgsm");

        result.Data.State.Should().Be(SearchState.LocalStrong);
        await _web.DidNotReceive().SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
