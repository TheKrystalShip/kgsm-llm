using FluentAssertions;

using Microsoft.Extensions.Options;

using TheKrystalShip.Llm.Conversation;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Llm.Tests;

/// <summary>
/// The canonical <see cref="SqliteConversationStore"/> over a throwaway temp DB: the append-only history
/// (turns + checkpoints), the model-context projection (replay from the latest checkpoint forward),
/// non-destructive compaction, isolation by id, durability across a new instance ("restart"), and a
/// faithful round-trip of a turn's tools (incl. the structured card) + thinking.
/// </summary>
public sealed class SqliteConversationStoreTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"kgsm-conv-store-{Guid.NewGuid():N}.db");

    private SqliteConversationStore Create() =>
        new(Options.Create(new ConversationOptions { DatabasePath = _dbPath }));

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch { /* best-effort temp cleanup */ }
    }

    private static ConversationTurnRecord Turn(
        string convId, string prompt, string? final,
        IReadOnlyList<RecordedToolCall>? tools = null, string? thinking = null, bool think = false,
        string? display = null) =>
        new()
        {
            ConversationId = convId,
            UserDisplay = display,
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            UserPrompt = prompt,
            SystemPromptHash = "h",
            Tools = tools ?? Array.Empty<RecordedToolCall>(),
            Iterations = 1,
            Outcome = final is null ? TurnOutcome.Error : TurnOutcome.Ok,
            Think = think,
            Thinking = thinking,
            Final = final,
        };

    [Fact]
    public void Empty_ReturnsNothing()
    {
        var store = Create();
        store.GetHistory("nope").Should().BeEmpty();
        store.GetModelContext("nope").Should().BeEmpty();
    }

    [Fact]
    public void AppendTurn_ProjectsUserThenFinal()
    {
        var store = Create();
        store.AppendTurn(Turn("c1", "is terraria up?", "Yes, it's running."));

        store.GetHistory("c1").Should().ContainSingle().Which.Kind.Should().Be(ConversationEntryKind.Turn);
        store.GetModelContext("c1").Select(m => (m.Role, m.Content)).Should().Equal(
            (LlmRole.User, "is terraria up?"), (LlmRole.Assistant, "Yes, it's running."));
    }

    [Fact]
    public void GetModelContext_WithoutCheckpoint_ReplaysEveryTurn_OmittingMissingFinals()
    {
        var store = Create();
        store.AppendTurn(Turn("c1", "q1", "a1"));
        store.AppendTurn(Turn("c1", "q2", null));   // a failed turn: user prompt only, no final
        store.AppendTurn(Turn("c1", "q3", "a3"));

        store.GetModelContext("c1").Select(m => (m.Role, m.Content)).Should().Equal(
            (LlmRole.User, "q1"), (LlmRole.Assistant, "a1"),
            (LlmRole.User, "q2"),
            (LlmRole.User, "q3"), (LlmRole.Assistant, "a3"));
    }

    [Fact]
    public void Checkpoint_IsNonDestructive_AndModelContextReplaysFromIt()
    {
        var store = Create();
        store.AppendTurn(Turn("c1", "q1", "a1"));
        store.AppendTurn(Turn("c1", "q2", "a2"));
        store.AddCheckpoint("c1", "summary of q1/q2");
        store.AppendTurn(Turn("c1", "q3", "a3"));

        // Full history keeps EVERYTHING — 2 turns + checkpoint + 1 turn.
        store.GetHistory("c1").Select(e => e.Kind).Should().Equal(
            ConversationEntryKind.Turn, ConversationEntryKind.Turn,
            ConversationEntryKind.Checkpoint, ConversationEntryKind.Turn);

        // Model context replays from the checkpoint forward: [summary], then q3.
        var ctx = store.GetModelContext("c1");
        ctx.Should().HaveCount(3);
        ctx[0].Role.Should().Be(LlmRole.Assistant);
        ctx[0].Content.Should().Contain("summary of q1/q2");
        ctx[1].Should().Match<LlmMessage>(m => m.Role == LlmRole.User && m.Content == "q3");
        ctx[2].Should().Match<LlmMessage>(m => m.Role == LlmRole.Assistant && m.Content == "a3");
    }

    [Fact]
    public void Conversations_AreIsolatedById()
    {
        var store = Create();
        store.AppendTurn(Turn("c1", "for c1", "r1"));
        store.AppendTurn(Turn("c2", "for c2", "r2"));

        store.GetHistory("c1").Should().ContainSingle();
        store.GetModelContext("c1").Should().Contain(m => m.Content == "for c1");
        store.GetModelContext("c2").Should().Contain(m => m.Content == "for c2");
        store.GetModelContext("c1").Should().NotContain(m => m.Content == "for c2");
    }

    [Fact]
    public void History_SurvivesANewStoreInstance()
    {
        // The point of the SQLite backing: a restart (fresh store, same file) keeps the conversation.
        Create().AppendTurn(Turn("c1", "before restart", "ok"));

        Create().GetModelContext("c1").Select(m => m.Content)
            .Should().ContainInOrder("before restart", "ok");
    }

    [Fact]
    public void ListConversations_IndexesScopeKeyAndItsChildren_MostRecentFirst_WithFirstPromptTitle()
    {
        var store = Create();
        // The bare per-user conversation + two per-chat children, appended oldest→newest.
        store.AppendTurn(Turn("web:u1", "bare-key prompt", "r"));
        store.AppendTurn(Turn("web:u1:chatA", "first prompt of A", "ra1"));
        store.AppendTurn(Turn("web:u1:chatA", "second prompt of A", "ra2"));
        store.AppendTurn(Turn("web:u1:chatB", "only prompt of B", "rb"));

        var list = store.ListConversations("web:u1");

        // chatB is most recently active → first; titles are each conversation's FIRST prompt.
        list.Select(c => c.ConversationId).Should().Equal("web:u1:chatB", "web:u1:chatA", "web:u1");
        list.Single(c => c.ConversationId == "web:u1:chatA").Title.Should().Be("first prompt of A");
        list.Single(c => c.ConversationId == "web:u1:chatA").TurnCount.Should().Be(2);
        list.Single(c => c.ConversationId == "web:u1").Title.Should().Be("bare-key prompt");
    }

    [Fact]
    public void ListConversations_DoesNotLeakAcrossAPrefixSharingUser()
    {
        var store = Create();
        store.AppendTurn(Turn("web:u1", "mine", "r"));
        store.AppendTurn(Turn("web:u1:c", "mine too", "r"));
        store.AppendTurn(Turn("web:u12", "NOT mine — u12 only shares a prefix with u1", "r"));
        store.AppendTurn(Turn("web:u12:c", "also NOT mine", "r"));

        store.ListConversations("web:u1").Select(c => c.ConversationId)
            .Should().BeEquivalentTo("web:u1", "web:u1:c");
    }

    [Fact]
    public void ListConversations_CountsTurnsNotCheckpoints()
    {
        var store = Create();
        store.AppendTurn(Turn("web:u1:c", "q1", "a1"));
        store.AddCheckpoint("web:u1:c", "recap");
        store.AppendTurn(Turn("web:u1:c", "q2", "a2"));

        store.ListConversations("web:u1").Should().ContainSingle()
            .Which.TurnCount.Should().Be(2);   // the checkpoint is not a turn
    }

    [Fact]
    public void SoftDelete_HidesFromList_ButKeepsTheTranscriptAndCorpus()
    {
        var store = Create();
        store.AppendTurn(Turn("web:u1:chatA", "keep me", "ok"));
        store.AppendTurn(Turn("web:u1:chatB", "delete me", "ok"));

        store.SoftDelete("web:u1:chatB");

        // Gone from the reverse-path list…
        store.ListConversations("web:u1").Select(c => c.ConversationId).Should().Equal("web:u1:chatA");
        // …but the append-only corpus is intact: the transcript and the model projection still have it.
        store.GetHistory("web:u1:chatB").Should().ContainSingle()
            .Which.Kind.Should().Be(ConversationEntryKind.Turn);
        store.GetModelContext("web:u1:chatB").Should().Contain(m => m.Content == "delete me");
    }

    [Fact]
    public void SoftDelete_SurvivesARestart_AndDoesNotThrowOnHistoryRead()
    {
        // The tombstone is durable (it's just another append-only row) and GetHistory must skip it — its
        // empty payload must never reach the turn deserializer.
        Create().AppendTurn(Turn("web:u1:c", "q1", "a1"));
        Create().SoftDelete("web:u1:c");

        var reopened = Create();
        reopened.ListConversations("web:u1").Should().BeEmpty();
        reopened.GetHistory("web:u1:c").Should().ContainSingle()        // tombstone skipped, turn kept
            .Which.Turn!.UserPrompt.Should().Be("q1");
    }

    [Fact]
    public void SoftDelete_ThenAResumingTurn_UnhidesTheConversation()
    {
        // Append-only + latest-entry-wins: a turn after the tombstone supersedes it (a resume).
        var store = Create();
        store.AppendTurn(Turn("web:u1:chatA", "first", "r1"));
        store.SoftDelete("web:u1:chatA");
        store.ListConversations("web:u1").Should().BeEmpty();

        store.AppendTurn(Turn("web:u1:chatA", "resumed", "r2"));
        store.ListConversations("web:u1").Should().ContainSingle().Which.TurnCount.Should().Be(2);
    }

    [Fact]
    public void Turn_WithToolsThinkingAndCard_RoundTripsThroughHistory()
    {
        var store = Create();
        var card = new Dictionary<string, object?> { ["overall"] = "warn", ["passed"] = 1 };
        var tools = new[]
        {
            new RecordedToolCall(
                new Tool("run_health_check"),
                new Dictionary<string, string?> { ["instance"] = "factorio" },
                "passed with warnings", 42, card),
        };
        store.AppendTurn(Turn("c1", "check it", "all good", tools: tools, thinking: "let me check the health", think: true));

        // Read back through a NEW instance to prove durability AND faithful (de)serialisation.
        var turn = Create().GetHistory("c1").Should().ContainSingle().Subject.Turn!;
        turn.UserPrompt.Should().Be("check it");
        turn.Final.Should().Be("all good");
        turn.Think.Should().BeTrue();
        turn.Thinking.Should().Be("let me check the health");
        turn.Outcome.Should().Be(TurnOutcome.Ok);

        var tool = turn.Tools.Should().ContainSingle().Subject;
        tool.Name.Name.Should().Be("run_health_check");
        tool.Arguments.Should().Contain("instance", "factorio");
        tool.Summary.Should().Be("passed with warnings");
        tool.DurationMs.Should().Be(42);
        tool.Card.Should().NotBeNull();   // the §5·a structured card survived the round-trip (as JSON)
    }

    // ---- the review path: include-deleted, per-conversation signal, and the actor index ----------

    private static ConversationTurnRecord Outcome(string convId, TurnOutcome outcome, string? display = null) =>
        Turn(convId, "p", "r", display: display) with { Outcome = outcome };

    [Fact]
    public void ListConversations_IncludeDeleted_ReturnsTheHiddenOnesFlagged()
    {
        // A review surface asks for what the owner's own list hides — the transcript was never erased,
        // and a conversation someone deleted is exactly what a tuning review wants to see.
        var store = Create();
        store.AppendTurn(Turn("web:u1:chatA", "kept", "r"));
        store.AppendTurn(Turn("web:u1:chatB", "hidden", "r"));
        store.SoftDelete("web:u1:chatB");

        store.ListConversations("web:u1").Select(c => c.ConversationId).Should().Equal("web:u1:chatA");

        var all = store.ListConversations("web:u1", includeDeleted: true);
        all.Select(c => c.ConversationId).Should().BeEquivalentTo("web:u1:chatA", "web:u1:chatB");
        all.Single(c => c.ConversationId == "web:u1:chatB").Deleted.Should().BeTrue();
        all.Single(c => c.ConversationId == "web:u1:chatA").Deleted.Should().BeFalse();
    }

    [Fact]
    public void ListConversations_TalliesTheOutcomesThatMarkATurnForReview()
    {
        var store = Create();
        store.AppendTurn(Outcome("web:u1:c", TurnOutcome.Ok));
        store.AppendTurn(Outcome("web:u1:c", TurnOutcome.Error));
        store.AppendTurn(Outcome("web:u1:c", TurnOutcome.Error));
        store.AppendTurn(Outcome("web:u1:c", TurnOutcome.CapHit));

        var summary = store.ListConversations("web:u1").Should().ContainSingle().Subject;
        summary.TurnCount.Should().Be(4);
        summary.ErrorTurns.Should().Be(2);
        summary.CapHitTurns.Should().Be(1);
    }

    [Fact]
    public void ListConversations_CarriesTheNewestRecordedName_AndNullWhenNoTurnHasOne()
    {
        var store = Create();
        store.AppendTurn(Turn("web:u1:c", "first", "r", display: "Old Name"));
        store.AppendTurn(Turn("web:u1:c", "second", "r", display: "New Name"));
        store.AppendTurn(Turn("web:u2:c", "anon", "r"));   // no name supplied by the host

        var list = Create().ListConversations("web:u1");
        list.Should().ContainSingle().Which.UserDisplay.Should().Be("New Name");
        Create().ListConversations("web:u2").Should().ContainSingle().Which.UserDisplay.Should().BeNull();
    }

    [Fact]
    public void ListActors_GroupsByTheUserSegment_MostRecentFirst()
    {
        var store = Create();
        store.AppendTurn(Turn("web:u1:chatA", "a", "r", display: "Ana"));
        store.AppendTurn(Turn("web:u1:chatB", "b", "r"));
        store.AppendTurn(Turn("web:u1", "bare", "r"));        // the pre-per-chat bare key is the same actor
        store.AppendTurn(Turn("web:u2:chatA", "c", "r"));

        var actors = Create().ListActors("web");
        actors.Select(a => a.UserId).Should().BeEquivalentTo("u1", "u2");

        var u1 = actors.Single(a => a.UserId == "u1");
        u1.Surface.Should().Be("web");
        u1.ConversationCount.Should().Be(3);
        u1.TurnCount.Should().Be(3);
        u1.UserDisplay.Should().Be("Ana");      // the one turn that carried a name
        u1.DeletedCount.Should().Be(0);

        // Most-recently-active first: u2's turn was appended last.
        actors[0].UserId.Should().Be("u2");
    }

    [Fact]
    public void ListActors_CountsSoftDeletedConversationsWithoutHidingTheActor()
    {
        // The actor's footprint is the whole log, deleted or not — hiding a conversation from its owner
        // must not make the person who held it disappear from a review index.
        var store = Create();
        store.AppendTurn(Turn("web:u1:chatA", "kept", "r"));
        store.AppendTurn(Turn("web:u1:chatB", "gone", "r"));
        store.SoftDelete("web:u1:chatB");

        var actor = Create().ListActors("web").Should().ContainSingle().Subject;
        actor.ConversationCount.Should().Be(2);
        actor.DeletedCount.Should().Be(1);
        actor.TurnCount.Should().Be(2);
    }

    [Fact]
    public void ListActors_IsScopedToItsSurface()
    {
        var store = Create();
        store.AppendTurn(Turn("web:u1:c", "web", "r"));
        store.AppendTurn(Turn("cli:abc123", "cli", "r"));

        Create().ListActors("web").Should().ContainSingle().Which.UserId.Should().Be("u1");
        Create().ListActors("cli").Should().ContainSingle().Which.UserId.Should().Be("abc123");
    }

    [Fact]
    public void ListActors_DoesNotConfuseUsersWhoseIdsShareAPrefix()
    {
        var store = Create();
        store.AppendTurn(Turn("web:u1:c", "a", "r"));
        store.AppendTurn(Turn("web:u10:c", "b", "r"));

        Create().ListActors("web").Select(a => a.UserId).Should().BeEquivalentTo("u1", "u10");
    }

    [Fact]
    public void UserDisplay_RoundTripsOntoTheStoredTurn()
    {
        var store = Create();
        store.AppendTurn(Turn("web:u1:c", "hello", "hi", display: "Ana Example"));

        Create().GetHistory("web:u1:c").Should().ContainSingle()
            .Which.Turn!.UserDisplay.Should().Be("Ana Example");
    }

    // ── GetStats — the whole-corpus roll-up behind the operator overview. ─────────────────────────

    private static ConversationTurnRecord StatTurn(
        string convId, TurnOutcome outcome, int iterations = 1, bool think = false,
        string? promptHash = "h1", int durationMs = 1000,
        IReadOnlyList<RecordedToolCall>? tools = null, LlmUsage? usage = null,
        DateTimeOffset? startedAt = null)
    {
        var started = startedAt ?? DateTimeOffset.UtcNow;
        return new ConversationTurnRecord
        {
            ConversationId = convId,
            StartedAt = started,
            CompletedAt = started.AddMilliseconds(durationMs),
            UserPrompt = "p",
            SystemPromptHash = promptHash!,
            Tools = tools ?? Array.Empty<RecordedToolCall>(),
            Iterations = iterations,
            Outcome = outcome,
            Think = think,
            Final = "r",
            Usage = usage,
        };
    }

    [Fact]
    public void GetStats_OnAnEmptyCorpus_CountsZeroAndMeasuresNothing()
    {
        // The load-bearing distinction: a count is 0 because it genuinely did not happen, while an
        // unmeasured distribution is null. A zero median would read as "instant", which is a lie.
        var stats = Create().GetStats("web");

        stats.Turns.Should().Be(0);
        stats.Conversations.Should().Be(0);
        stats.MedianTurnMs.Should().BeNull();
        stats.P95TurnMs.Should().BeNull();
        stats.MedianIterations.Should().BeNull();
        stats.MedianContextPercent.Should().BeNull();
        stats.ContextWindow.Should().BeNull();
        stats.Tools.Should().BeEmpty();
    }

    [Fact]
    public void GetStats_TalliesEveryOutcomeSeparately()
    {
        var store = Create();
        store.AppendTurn(StatTurn("web:u:a", TurnOutcome.Ok));
        store.AppendTurn(StatTurn("web:u:a", TurnOutcome.Ok));
        store.AppendTurn(StatTurn("web:u:b", TurnOutcome.Error));
        store.AppendTurn(StatTurn("web:u:b", TurnOutcome.CapHit));
        store.AppendTurn(StatTurn("web:u:c", TurnOutcome.Cancelled));

        var stats = Create().GetStats("web");

        stats.Turns.Should().Be(5);
        stats.OkTurns.Should().Be(2);
        stats.ErrorTurns.Should().Be(1);
        stats.CapHitTurns.Should().Be(1);
        stats.CancelledTurns.Should().Be(1);
        stats.UnrecordedOutcomeTurns.Should().Be(0);
        stats.Conversations.Should().Be(3);
        stats.Actors.Should().Be(1);
    }

    [Fact]
    public void GetStats_CountsSoftDeletedConversationsAndTheirTurns()
    {
        // A deleted conversation's turns are part of what the assistant actually did. Dropping them
        // would understate the very corpus the review is judging.
        var store = Create();
        store.AppendTurn(StatTurn("web:u:keep", TurnOutcome.Ok));
        store.AppendTurn(StatTurn("web:u:gone", TurnOutcome.Error));
        store.SoftDelete("web:u:gone");

        var stats = Create().GetStats("web");

        stats.Conversations.Should().Be(2);
        stats.DeletedConversations.Should().Be(1);
        stats.Turns.Should().Be(2);
        stats.ErrorTurns.Should().Be(1, "a hidden conversation's failure still happened");
    }

    [Fact]
    public void GetStats_ReportsNearestRankPercentilesOverAnswerTimes()
    {
        var store = Create();
        foreach (var ms in new[] { 100, 200, 300, 400, 5000 })
            store.AppendTurn(StatTurn("web:u:a", TurnOutcome.Ok, durationMs: ms));

        var stats = Create().GetStats("web");

        stats.MedianTurnMs.Should().Be(300);
        stats.P95TurnMs.Should().Be(5000);
        stats.MaxTurnMs.Should().Be(5000);
    }

    [Fact]
    public void GetStats_ExplodesToolCallsAndCountsErrorOutputsAsFailures()
    {
        // "Failed" is read off the recorded output's "Error: …" convention — a measurement of what the
        // dispatcher wrote, not an inference.
        var store = Create();
        store.AppendTurn(StatTurn("web:u:a", TurnOutcome.Ok, tools: new[]
        {
            new RecordedToolCall(new Tool("get_status"), new Dictionary<string, string?>(), "fine", 100),
            new RecordedToolCall(new Tool("get_status"), new Dictionary<string, string?>(), "fine", 300),
            new RecordedToolCall(new Tool("open_ports"), new Dictionary<string, string?>(), "Error: nope", 0),
        }));
        store.AppendTurn(StatTurn("web:u:a", TurnOutcome.Ok));

        var stats = Create().GetStats("web");

        stats.ToolCalls.Should().Be(3);
        stats.TurnsWithoutTool.Should().Be(1);
        var status = stats.Tools.Single(t => t.Name == "get_status");
        status.Calls.Should().Be(2);
        status.MaxMs.Should().Be(300);
        status.FailedCalls.Should().Be(0);
        var ports = stats.Tools.Single(t => t.Name == "open_ports");
        ports.Calls.Should().Be(1);
        ports.FailedCalls.Should().Be(1);
    }

    [Fact]
    public void GetStats_KeepsAToolNameNoCatalogDefines()
    {
        // A model that invents a tool is the single most useful signal in the set; the store reports
        // the name it recorded and leaves "is this in the catalog" to the surface that owns one.
        var store = Create();
        store.AppendTurn(StatTurn("web:u:a", TurnOutcome.Ok, tools: new[]
        {
            new RecordedToolCall(new Tool("google_search"), new Dictionary<string, string?>(),
                "Error: 'google_search' is not a known tool.", 0),
        }));

        var stats = Create().GetStats("web");

        stats.Tools.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Name = "google_search", Calls = 1, FailedCalls = 1 });
    }

    [Fact]
    public void GetStats_BucketsTurnsByTheSystemPromptTheyRanUnder()
    {
        var store = Create();
        store.AppendTurn(StatTurn("web:u:a", TurnOutcome.Ok, promptHash: "old"));
        store.AppendTurn(StatTurn("web:u:a", TurnOutcome.Error, promptHash: "old"));
        store.AppendTurn(StatTurn("web:u:a", TurnOutcome.Ok, promptHash: "new"));

        var stats = Create().GetStats("web");

        stats.PromptVersions.Should().HaveCount(2);
        var old = stats.PromptVersions.Single(p => p.Hash == "old");
        old.Turns.Should().Be(2);
        old.OkTurns.Should().Be(1);
        stats.PromptVersions.Single(p => p.Hash == "new").OkTurns.Should().Be(1);
    }

    [Fact]
    public void GetStats_ReportsTheContextWindowOnlyWhenEveryTurnAgrees()
    {
        // Two windows are two denominators; one number over them would describe neither.
        var store = Create();
        store.AppendTurn(StatTurn("web:u:a", TurnOutcome.Ok,
            usage: new LlmUsage(90, 10, 1000)));
        var single = Create().GetStats("web");
        single.ContextWindow.Should().Be(1000);
        single.MedianContextPercent.Should().Be(10);

        store.AppendTurn(StatTurn("web:u:a", TurnOutcome.Ok,
            usage: new LlmUsage(90, 10, 2000)));

        Create().GetStats("web").ContextWindow.Should().BeNull();
    }

    [Fact]
    public void GetStats_GroupsActivityByUtcDay()
    {
        var store = Create();
        var day = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        store.AppendTurn(StatTurn("web:u:a", TurnOutcome.Ok, startedAt: day));
        store.AppendTurn(StatTurn("web:u:a", TurnOutcome.Ok, startedAt: day.AddHours(2)));
        store.AppendTurn(StatTurn("web:u:a", TurnOutcome.Ok, startedAt: day.AddDays(1)));

        var stats = Create().GetStats("web");

        stats.Activity.Should().HaveCount(2);
        stats.Activity[0].Should().BeEquivalentTo(new { Date = "2026-08-01", Turns = 2 });
        stats.Activity[1].Should().BeEquivalentTo(new { Date = "2026-08-02", Turns = 1 });
    }

    [Fact]
    public void GetStats_IsScopedToItsSurface()
    {
        var store = Create();
        store.AppendTurn(StatTurn("web:u:a", TurnOutcome.Ok));
        store.AppendTurn(StatTurn("cli:u:a", TurnOutcome.Error));

        var web = Create().GetStats("web");
        web.Turns.Should().Be(1);
        web.ErrorTurns.Should().Be(0);
    }

    [Fact]
    public void GetStats_CountsThinkingTurns()
    {
        var store = Create();
        store.AppendTurn(StatTurn("web:u:a", TurnOutcome.Ok, think: true));
        store.AppendTurn(StatTurn("web:u:a", TurnOutcome.Ok));

        Create().GetStats("web").ThinkingTurns.Should().Be(1);
    }

    // ── Turn feedback: the only signal in the corpus that says whether an answer was any GOOD ──────────

    [Fact]
    public void AppendTurn_ReturnsTheIdThatAddressesThatTurn()
    {
        var store = Create();
        var first = store.AppendTurn(Turn("c1", "q1", "a1"));
        var second = store.AppendTurn(Turn("c1", "q2", "a2"));

        first.Should().BeGreaterThan(0);
        second.Should().NotBe(first);
        store.GetHistory("c1").Select(e => e.Id).Should().Equal(first, second);
    }

    [Fact]
    public void SetTurnFeedback_RidesBackOnTheTurnItJudges()
    {
        var store = Create();
        var turnId = store.AppendTurn(Turn("c1", "q1", "a1"));
        store.AppendTurn(Turn("c1", "q2", "a2"));

        store.SetTurnFeedback("c1", turnId, TurnFeedbackRating.Down, "  told me the wrong port  ").Should().BeTrue();

        var history = Create().GetHistory("c1");
        history[0].Feedback.Should().BeEquivalentTo(
            new { Rating = TurnFeedbackRating.Down, Note = "told me the wrong port" },
            o => o.ExcludingMissingMembers());
        // An unrated turn carries no verdict at all — never a neutral one.
        history[1].Feedback.Should().BeNull();
    }

    [Fact]
    public void SetTurnFeedback_LatestVerdictWins_AndClearingRemovesIt()
    {
        var store = Create();
        var turnId = store.AppendTurn(Turn("c1", "q1", "a1"));

        store.SetTurnFeedback("c1", turnId, TurnFeedbackRating.Down, "wrong");
        store.SetTurnFeedback("c1", turnId, TurnFeedbackRating.Up, null);
        Create().GetHistory("c1")[0].Feedback!.Rating.Should().Be(TurnFeedbackRating.Up);

        store.SetTurnFeedback("c1", turnId, null, null);
        Create().GetHistory("c1")[0].Feedback.Should().BeNull();
    }

    [Fact]
    public void SetTurnFeedback_RefusesATurnOutsideTheNamedConversation()
    {
        // Entry ids ascend across the WHOLE log, so a caller correctly scoped to its own conversation
        // could otherwise rate a stranger's turn just by naming a neighbouring id.
        var store = Create();
        var mine = store.AppendTurn(Turn("web:me:a", "q", "a"));
        var theirs = store.AppendTurn(Turn("web:someone-else:a", "q", "a"));

        store.SetTurnFeedback("web:me:a", theirs, TurnFeedbackRating.Down, "not mine to rate").Should().BeFalse();
        store.SetTurnFeedback("web:me:a", mine, TurnFeedbackRating.Down, null).Should().BeTrue();

        Create().GetHistory("web:someone-else:a")[0].Feedback.Should().BeNull();
    }

    [Fact]
    public void Feedback_IsNeverReplayedIntoTheModelContext()
    {
        // A verdict is an analysis record, like Thinking. Feeding it back as conversation would change
        // the next answer.
        var store = Create();
        var turnId = store.AppendTurn(Turn("c1", "q1", "a1"));
        store.SetTurnFeedback("c1", turnId, TurnFeedbackRating.Down, "that was useless");

        Create().GetModelContext("c1").Select(m => m.Content).Should().Equal("q1", "a1");
    }

    [Fact]
    public void Feedback_DoesNotCountAsConversationActivity()
    {
        // Rating an old chat must not reorder someone's history list, so a verdict may not move the
        // conversation's timestamps.
        var store = Create();
        var older = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var turnId = store.AppendTurn(Turn("web:u:old", "q", "a") with { StartedAt = older, CompletedAt = older });
        store.AppendTurn(Turn("web:u:new", "q", "a"));

        var before = Create().ListConversations("web:u").Select(c => c.ConversationId).ToList();
        store.SetTurnFeedback("web:u:old", turnId, TurnFeedbackRating.Up, null);

        Create().ListConversations("web:u").Select(c => c.ConversationId).Should().Equal(before);
        Create().ListConversations("web:u").Single(c => c.ConversationId == "web:u:old")
            .LastActivityAt.Should().Be(older);
    }

    [Fact]
    public void Feedback_DoesNotResurrectASoftDeletedConversation()
    {
        // Deleted-ness is "the newest tombstone out-ids every content entry". A verdict is bookkeeping
        // about a turn, not new content, so rating one inside a hidden chat must not un-hide it.
        var store = Create();
        var turnId = store.AppendTurn(Turn("web:u:a", "q", "a"));
        store.SoftDelete("web:u:a");

        store.SetTurnFeedback("web:u:a", turnId, TurnFeedbackRating.Down, "still bad");

        Create().ListConversations("web:u").Should().BeEmpty();
        Create().ListConversations("web:u", includeDeleted: true).Should().ContainSingle()
            .Which.Deleted.Should().BeTrue();
    }

    [Fact]
    public void ListConversations_CountsTheNegativeVerdictsThatStand()
    {
        var store = Create();
        var bad = store.AppendTurn(Turn("web:u:a", "q1", "a1"));
        var alsoBad = store.AppendTurn(Turn("web:u:a", "q2", "a2"));
        var recanted = store.AppendTurn(Turn("web:u:a", "q3", "a3"));
        store.SetTurnFeedback("web:u:a", bad, TurnFeedbackRating.Down, null);
        store.SetTurnFeedback("web:u:a", alsoBad, TurnFeedbackRating.Down, null);
        store.SetTurnFeedback("web:u:a", recanted, TurnFeedbackRating.Down, null);
        store.SetTurnFeedback("web:u:a", recanted, TurnFeedbackRating.Up, null);

        Create().ListConversations("web:u").Single().NegativeTurns.Should().Be(2);
    }

    [Fact]
    public void GetStats_ReportsSatisfactionWithItsCoverage()
    {
        var store = Create();
        var good = store.AppendTurn(StatTurn("web:u:a", TurnOutcome.Ok));
        var bad = store.AppendTurn(StatTurn("web:u:a", TurnOutcome.Ok));
        store.AppendTurn(StatTurn("web:u:a", TurnOutcome.Ok));   // never rated
        store.SetTurnFeedback("web:u:a", good, TurnFeedbackRating.Up, null);
        store.SetTurnFeedback("web:u:a", bad, TurnFeedbackRating.Down, "wrong answer");

        var stats = Create().GetStats("web");

        stats.Turns.Should().Be(3);
        stats.RatedTurns.Should().Be(2);          // the coverage a rate is meaningless without
        stats.PositiveTurns.Should().Be(1);
        stats.NegativeTurns.Should().Be(1);
        stats.SatisfactionPercent.Should().Be(50);
    }

    [Fact]
    public void GetStats_ReportsNoSatisfactionRateWhenNothingWasRated()
    {
        // Null, not 0% — a corpus nobody voted on has no satisfaction rate, and zero would assert that
        // every answer failed. Same rule the duration percentiles follow.
        var store = Create();
        store.AppendTurn(StatTurn("web:u:a", TurnOutcome.Ok));

        var stats = Create().GetStats("web");
        stats.SatisfactionPercent.Should().BeNull();
        stats.RatedTurns.Should().Be(0);
    }

    [Fact]
    public void GetStats_BucketsVerdictsByPromptVersion()
    {
        // The whole reason the prompt hash is recorded: change the prompt, and the next bucket's
        // thumbs-down rate is directly comparable to the last.
        var store = Create();
        var oldBad = store.AppendTurn(StatTurn("web:u:a", TurnOutcome.Ok, promptHash: "old"));
        var newGood = store.AppendTurn(StatTurn("web:u:a", TurnOutcome.Ok, promptHash: "new"));
        store.SetTurnFeedback("web:u:a", oldBad, TurnFeedbackRating.Down, null);
        store.SetTurnFeedback("web:u:a", newGood, TurnFeedbackRating.Up, null);

        var versions = Create().GetStats("web").PromptVersions;

        versions.Single(v => v.Hash == "old").Should().BeEquivalentTo(
            new { NegativeTurns = 1, RatedTurns = 1 }, o => o.ExcludingMissingMembers());
        versions.Single(v => v.Hash == "new").Should().BeEquivalentTo(
            new { NegativeTurns = 0, RatedTurns = 1 }, o => o.ExcludingMissingMembers());
    }

    [Fact]
    public void GetStats_SurfacesWhatPeopleWroteOnAThumbsDown()
    {
        var store = Create();
        var bad = store.AppendTurn(StatTurn("web:u:a", TurnOutcome.Ok));
        var silent = store.AppendTurn(StatTurn("web:u:a", TurnOutcome.Ok));
        var praised = store.AppendTurn(StatTurn("web:u:a", TurnOutcome.Ok));
        store.SetTurnFeedback("web:u:a", bad, TurnFeedbackRating.Down, "invented a server that doesn't exist");
        store.SetTurnFeedback("web:u:a", silent, TurnFeedbackRating.Down, null);
        store.SetTurnFeedback("web:u:a", praised, TurnFeedbackRating.Up, "nice");

        var notes = Create().GetStats("web").FeedbackNotes;

        // Only explained thumbs-down: an unexplained one has nothing to read, and a thumbs-up note is
        // not a complaint to triage.
        notes.Should().ContainSingle();
        notes[0].Note.Should().Be("invented a server that doesn't exist");
        notes[0].TurnId.Should().Be(bad);
        notes[0].ConversationId.Should().Be("web:u:a");
        notes[0].Prompt.Should().Be("p");
    }

    [Fact]
    public void Preferences_AreUnsetUntilSomethingSetsThem()
    {
        // Unset must NOT read as false: a conversation nobody has told anything falls to the host's
        // configured default, and answering false would override that configuration with a value
        // nobody chose.
        var standing = Create().GetPreferences("web:u:never-touched");

        standing.Think.Should().BeNull();
        standing.Autorun.Should().BeNull();
    }

    [Fact]
    public void Preferences_AreWrittenAsDeltas_SoTheTwoSwitchesAreIndependent()
    {
        var store = Create();
        store.SetPreferences("web:u:a", new ConversationPreferences(Think: true, Autorun: null));
        store.SetPreferences("web:u:a", new ConversationPreferences(Think: null, Autorun: true));

        // The second write said nothing about thinking, so it left it standing rather than clearing it.
        var standing = Create().GetPreferences("web:u:a");
        standing.Think.Should().BeTrue();
        standing.Autorun.Should().BeTrue();
    }

    [Fact]
    public void Preferences_ResolveLatestWins_PerField()
    {
        var store = Create();
        store.SetPreferences("web:u:a", new ConversationPreferences(true, true));
        store.SetPreferences("web:u:a", new ConversationPreferences(false, null));

        var standing = Create().GetPreferences("web:u:a");
        standing.Think.Should().BeFalse();
        standing.Autorun.Should().BeTrue();
    }

    [Fact]
    public void APreferenceAlone_DoesNotConjureAConversation()
    {
        // Flipping a switch on a chat that was never started leaves an id holding nothing but
        // bookkeeping. That is not a conversation — it has no beginning and no activity — so it must
        // not be listed, counted, or reported with null timestamps.
        var store = Create();
        store.SetPreferences("web:u:never-spoken", new ConversationPreferences(true, null));

        Create().ListConversations("web:u").Should().BeEmpty();
        Create().ListActors("web").Should().BeEmpty();
        Create().GetStats("web").Conversations.Should().Be(0);

        // The switch itself still stands — it just does not make the chat exist.
        Create().GetPreferences("web:u:never-spoken").Think.Should().BeTrue();
    }

    [Fact]
    public void Preferences_AreScopedToOneConversation()
    {
        var store = Create();
        store.SetPreferences("web:u:a", new ConversationPreferences(true, true));

        var other = store.GetPreferences("web:u:b");
        other.Think.Should().BeNull();
        other.Autorun.Should().BeNull("auto-run armed in one chat must not reach another");
    }

    [Fact]
    public void SettingNothing_WritesNothing()
    {
        var store = Create();
        store.AppendTurn(Turn("web:u:a", "hello", "hi"));
        var before = store.ListConversations("web:u").Single().LastActivityAt;

        store.SetPreferences("web:u:a", ConversationPreferences.Unset);

        // An append saying nothing would still sit in the log claiming a switch was touched.
        Create().ListConversations("web:u").Single().LastActivityAt.Should().Be(before);
    }

    [Fact]
    public void APreference_IsNotActivity_AndDoesNotReorderTheList()
    {
        var store = Create();
        store.AppendTurn(Turn("web:u:older", "first", "a"));
        store.AppendTurn(Turn("web:u:newer", "second", "b"));

        // Flipping a switch on the older chat is bookkeeping ABOUT it, not something that happened IN
        // it — so it must not jump the list.
        store.SetPreferences("web:u:older", new ConversationPreferences(true, null));

        Create().ListConversations("web:u").First().ConversationId.Should().Be("web:u:newer");
    }

    [Fact]
    public void APreference_IsNeverShownInTheTranscript()
    {
        var store = Create();
        store.AppendTurn(Turn("web:u:a", "hello", "hi"));
        store.SetPreferences("web:u:a", new ConversationPreferences(true, null));

        // Its payload is not a turn record; reaching the turn deserializer would put an empty bubble
        // in the conversation.
        var history = Create().GetHistory("web:u:a");
        history.Should().ContainSingle();
        history[0].Kind.Should().Be(ConversationEntryKind.Turn);
    }

    [Fact]
    public void CreateConversation_MakesAnEmptyConversationExistAndList()
    {
        var store = Create();
        store.CreateConversation("web:u:fresh").Should().BeTrue();

        // Started, not yet spoken into — and visible, so another device sees the chat that was opened.
        var listed = Create().ListConversations("web:u").Single();
        listed.ConversationId.Should().Be("web:u:fresh");
        listed.TurnCount.Should().Be(0);
        listed.Title.Should().BeNull();
    }

    [Fact]
    public void CreateConversation_IsIdempotent_AndNeverClaimsASecondBeginning()
    {
        var store = Create();
        store.AppendTurn(Turn("web:u:a", "hello", "hi"));

        store.CreateConversation("web:u:a").Should().BeFalse("it already exists");

        var listed = Create().ListConversations("web:u").Single();
        listed.TurnCount.Should().Be(1);
    }

    [Fact]
    public void CreateConversation_DoesNotResurrectADeletedOne()
    {
        var store = Create();
        store.AppendTurn(Turn("web:u:a", "hello", "hi"));
        store.SoftDelete("web:u:a");

        // The id is known, so there is nothing to create — and no new entry that could out-id the
        // tombstone and quietly un-hide the conversation.
        store.CreateConversation("web:u:a").Should().BeFalse();
        Create().ListConversations("web:u").Should().BeEmpty();
    }
}
