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
}
