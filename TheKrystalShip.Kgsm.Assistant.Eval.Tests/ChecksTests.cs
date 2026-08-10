using FluentAssertions;

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
    public void AnyOf_passes_if_any_subcheck_passes()
    {
        var check = C.AnyOf(Rubric.B_Routing, "x",
            C.CalledTool(LlmTools.Search), C.CalledTool(LlmTools.RunHealthCheck));
        check.Evaluate(Obs(tools: new[] { Tool(LlmTools.RunHealthCheck, ("instance_name", "factorio-test")) }), Fx).Should().BeTrue();
        check.Evaluate(Obs(), Fx).Should().BeFalse();
    }
}
