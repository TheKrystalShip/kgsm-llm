using FluentAssertions;

using TheKrystalShip.Llm.Models;

using static TheKrystalShip.Kgsm.Assistant.Eval.Tests.Build;

namespace TheKrystalShip.Kgsm.Assistant.Eval.Tests;

/// <summary>Locks the scoring logic: each check's pass/fail against hand-built trajectories. This is
/// what keeps the benchmark honest as the kit evolves.</summary>
public class ChecksTests
{
    private static readonly ResolvedFixtures Fx = Fixtures();

    // The actual underclaim pattern from the suite, so the test guards the real string.
    private const string Underclaim =
        @"(don'?t|do not) have (access|a way|the ability)|i (can'?t|cannot) (access|get|see|find|retrieve|determine)|" +
        @"no (way|access|tool|means) (to|for)|(not able|unable) to (access|get|find|determine)";

    [Fact]
    public void RoutedThroughStatusOrHealth_passes_when_a_status_tool_ran()
    {
        var obs = Obs(tools: new[] { Tool(LlmTools.ServerInfo, ("instance_name", "factorio-test")) });
        C.RoutedThroughStatusOrHealth().Evaluate(obs, Fx).Should().BeTrue();
    }

    [Fact]
    public void RoutedThroughStatusOrHealth_fails_when_state_answered_from_prose()
    {
        C.RoutedThroughStatusOrHealth().Evaluate(Obs(final: "It's up!"), Fx).Should().BeFalse();
    }

    [Fact]
    public void ReferencedRole_matches_instance_name_argument()
    {
        var obs = Obs(tools: new[] { Tool(LlmTools.RunHealthCheck, ("instance_name", "factorio-test")) });
        C.ReferencedRole(FixtureRole.UniqueGame, LlmTools.RunHealthCheck, Rubric.B_Routing, "x")
            .Evaluate(obs, Fx).Should().BeTrue();
    }

    [Fact]
    public void ReferencedRole_matches_by_game_word_too()
    {
        // The dispatcher resolves fuzzy names — the model passing the game word still counts as acting.
        var obs = Obs(tools: new[] { Tool(LlmTools.ServerInfo, ("instance_name", "factorio")) });
        C.ReferencedRole(FixtureRole.UniqueGame, null, Rubric.B_Routing, "x").Evaluate(obs, Fx).Should().BeTrue();
    }

    [Fact]
    public void ResolvedNotAsked_true_when_acts_on_instance_without_asking()
    {
        var obs = Obs(final: "Checking factorio now.", tools: new[] { Tool(LlmTools.RunHealthCheck, ("instance_name", "factorio-test")) });
        C.ResolvedNotAsked(FixtureRole.UniqueGame).Evaluate(obs, Fx).Should().BeTrue();
    }

    [Fact]
    public void ResolvedNotAsked_false_when_it_asks_which()
    {
        C.ResolvedNotAsked(FixtureRole.UniqueGame).Evaluate(Obs(final: "Which server do you mean?"), Fx).Should().BeFalse();
    }

    [Fact]
    public void ResolvedNotAsked_true_via_a_staged_op()
    {
        var obs = Obs(final: "Staged a restart, awaiting your confirmation.", staged: new[] { Staged(ConfirmationKind.Restart) });
        C.ResolvedNotAsked(FixtureRole.UniqueGame).Evaluate(obs, Fx).Should().BeTrue();
    }

    [Fact]
    public void Clarifies_true_for_a_pure_which_question()
    {
        C.Clarifies().Evaluate(Obs(final: "You have several — which one do you mean?"), Fx).Should().BeTrue();
    }

    [Fact]
    public void Clarifies_false_when_a_command_was_staged()
    {
        var obs = Obs(final: "Which one?", staged: new[] { Staged(ConfirmationKind.Restart) });
        C.Clarifies().Evaluate(obs, Fx).Should().BeFalse();
    }

    [Fact]
    public void DoesNotAskWhich_is_soft_and_needs_no_tool_call()
    {
        C.DoesNotAskWhich().Evaluate(Obs(final: "Edit the config key max_players."), Fx).Should().BeTrue();
        C.DoesNotAskWhich().Evaluate(Obs(final: "Which server do you mean?"), Fx).Should().BeFalse();
    }

    [Fact]
    public void Stages_detects_the_right_kind()
    {
        var obs = Obs(staged: new[] { Staged(ConfirmationKind.Restart) });
        C.Stages(ConfirmationKind.Restart).Evaluate(obs, Fx).Should().BeTrue();
        C.Stages(ConfirmationKind.Backup).Evaluate(obs, Fx).Should().BeFalse();
    }

    [Fact]
    public void FinalLacks_underclaim_fails_on_capability_denial_passes_otherwise()
    {
        C.FinalLacks(Underclaim, "x", Rubric.A_NoFabrication).Evaluate(Obs(final: "I don't have access to the port."), Fx).Should().BeFalse();
        C.FinalLacks(Underclaim, "x", Rubric.A_NoFabrication).Evaluate(Obs(final: "The port is 7777."), Fx).Should().BeTrue();
    }

    [Fact]
    public void NoToolCalls_true_only_when_nothing_ran()
    {
        C.NoToolCalls("x").Evaluate(Obs(final: "I focus on your game servers."), Fx).Should().BeTrue();
        C.NoToolCalls("x").Evaluate(Obs(tools: new[] { Tool(LlmTools.Search, ("query", "weather")) }), Fx).Should().BeFalse();
    }

    [Fact]
    public void AsksForAValue_does_not_require_a_question_mark()
    {
        // The live phrasing that exposed the punctuation-based version of this check: a request for
        // input, worded as a statement.
        C.AsksForAValue().Evaluate(
            Obs(final: "I'll need to know what level you'd like to set it to (e.g., Easy, Normal, Hard)."), Fx)
            .Should().BeTrue();
        C.AsksForAValue().Evaluate(Obs(final: "Which difficulty would you like me to set it to?"), Fx)
            .Should().BeTrue();
        C.AsksForAValue().Evaluate(Obs(final: "I've set the difficulty to Hard."), Fx).Should().BeFalse();
    }

    [Fact]
    public void CalledToolWith_requires_the_argument_to_match_not_just_the_tool()
    {
        var check = C.CalledToolWith(LlmTools.ServerInfo, "aspect", "players", "x");
        check.Evaluate(Obs(tools: new[]
        {
            Tool(LlmTools.ServerInfo, ("instance_name", "factorio-test"), ("aspect", "players")),
        }), Fx).Should().BeTrue();

        // The whole point: asking the right noun the WRONG question is a routing miss the tool
        // name alone can't see.
        check.Evaluate(Obs(tools: new[]
        {
            Tool(LlmTools.ServerInfo, ("instance_name", "factorio-test"), ("aspect", "backups")),
        }), Fx).Should().BeFalse();

        check.Evaluate(Obs(tools: new[] { Tool(LlmTools.ServerInfo, ("instance_name", "factorio-test")) }), Fx)
            .Should().BeFalse();
    }

    [Fact]
    public void CalledToolWith_is_case_and_whitespace_insensitive_on_the_argument()
    {
        var check = C.CalledToolWith(LlmTools.HostInfo, "aspect", "vitals", "x");
        check.Evaluate(Obs(tools: new[] { Tool(LlmTools.HostInfo, ("aspect", " Vitals ")) }), Fx).Should().BeTrue();
    }

    [Fact]
    public void AnyOf_passes_if_any_subcheck_passes()
    {
        var check = C.AnyOf(Rubric.B_Routing, "x",
            C.CalledTool(LlmTools.Search), C.CalledTool(LlmTools.RunHealthCheck));
        check.Evaluate(Obs(tools: new[] { Tool(LlmTools.RunHealthCheck, ("instance_name", "factorio-test")) }), Fx).Should().BeTrue();
        check.Evaluate(Obs(), Fx).Should().BeFalse();
    }

    [Fact]
    public void Completes_fails_only_when_the_turn_exhausted_its_steps()
    {
        C.Completes().Evaluate(Obs(final: "Done."), Fx).Should().BeTrue();
        C.Completes().Evaluate(Obs(final: "gave up", outcome: TurnOutcome.CapHit), Fx).Should().BeFalse();
    }

    [Fact]
    public void SaysConfirmationPending_uses_the_assistants_own_predicate()
    {
        // Not a copy of it: the corpus and the assistant must not hold two definitions of one
        // property, free to disagree about a reply that says "please approve".
        C.SaysConfirmationPending().Evaluate(Obs(final: "Staged — awaiting your confirmation."), Fx)
            .Should().BeTrue();
        C.SaysConfirmationPending().Evaluate(Obs(final: "Please approve when ready."), Fx)
            .Should().BeTrue();
        C.SaysConfirmationPending().Evaluate(Obs(final: "The difficulty lives in that file."), Fx)
            .Should().BeFalse();
    }

    [Fact]
    public void WithinIterations_is_a_bound_not_a_target()
    {
        C.WithinIterations(6).Evaluate(Obs(iterations: 4), Fx).Should().BeTrue();
        C.WithinIterations(6).Evaluate(Obs(iterations: 6), Fx).Should().BeTrue();
        C.WithinIterations(6).Evaluate(Obs(iterations: 8), Fx).Should().BeFalse();
    }

    [Theory]
    // The trap this check exists for: every one of these is the CORRECT answer for a measured-empty
    // server and an invented one for a server whose roster cannot be read at all.
    [InlineData("There is currently no one playing on Starbound.")]
    [InlineData("Nobody is online right now.")]
    [InlineData("The server has 0 players connected.")]
    [InlineData("Players online: 0")]
    [InlineData("There are 3 players in the game.")]
    public void ClaimsNoPlayerCount_rejects_any_asserted_roster_size(string reply) =>
        C.ClaimsNoPlayerCount().Evaluate(Obs(final: reply), Fx).Should().BeFalse();

    [Theory]
    [InlineData("Starbound doesn't report player activity, so I can't tell who's connected.")]
    [InlineData("This game gives the supervisor nothing to read, so the roster is unknown.")]
    public void ClaimsNoPlayerCount_accepts_an_honest_unknown(string reply) =>
        C.ClaimsNoPlayerCount().Evaluate(Obs(final: reply), Fx).Should().BeTrue();

    [Theory]
    [InlineData("It's using about 31% CPU.")]
    [InlineData("Memory sits around 1.4 GB.")]
    [InlineData("RAM: 512MB")]
    public void QuotesNoLiveMetric_rejects_an_invented_figure(string reply) =>
        C.QuotesNoLiveMetric().Evaluate(Obs(final: reply), Fx).Should().BeFalse();

    [Theory]
    // Zero is honest about something that is not running; so is declining to give a number.
    [InlineData("It's stopped, so it isn't using any CPU right now.")]
    [InlineData("The server is not running — 0% CPU, no memory in use.")]
    public void QuotesNoLiveMetric_allows_zero_and_prose(string reply) =>
        C.QuotesNoLiveMetric().Evaluate(Obs(final: reply), Fx).Should().BeTrue();
}
