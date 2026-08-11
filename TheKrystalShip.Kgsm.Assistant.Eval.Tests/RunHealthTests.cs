using FluentAssertions;

using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Eval.Tests;

/// <summary>
/// A dead model endpoint and a badly-behaving model produce the same-looking scorecard — every check
/// red — so these lock the one thing that tells them apart.
/// </summary>
public class RunHealthTests
{
    private static void Ok(RunHealth h, int n) { for (var i = 0; i < n; i++) h.Record(TurnOutcome.Ok); }

    [Fact]
    public void A_single_errored_turn_is_noise_and_never_aborts()
    {
        // A local model occasionally returns an empty reply after tool calls. Aborting on one would
        // throw away a whole run over a known flake.
        var h = new RunHealth();
        Ok(h, 20);
        h.Record(TurnOutcome.Error);
        Ok(h, 20);

        h.TurnsErrored.Should().Be(1);
        h.IsDegraded.Should().BeFalse("a single known flake never condemns a run, at any run length");
    }

    [Fact]
    public void Scattered_errors_never_abort_however_many_there_are()
    {
        var h = new RunHealth();
        for (var i = 0; i < 40; i++) { h.Record(TurnOutcome.Error); Ok(h, 1); }

        h.TurnsErrored.Should().Be(40);
        h.IsDegraded.Should().BeTrue("half the turns failed, so the run is not trustworthy");
    }

    [Fact]
    public void Enough_failures_in_a_row_aborts()
    {
        var h = new RunHealth();
        Ok(h, 10);

        var abort = () =>
        {
            for (var i = 0; i < RunHealth.ConsecutiveErrorsToAbort; i++) h.Record(TurnOutcome.Error);
        };

        abort.Should().Throw<EvalEndpointDownException>()
            .WithMessage("*endpoint is not answering*");
    }

    [Fact]
    public void A_successful_turn_clears_the_consecutive_count()
    {
        var h = new RunHealth();
        for (var round = 0; round < 5; round++)
        {
            for (var i = 0; i < RunHealth.ConsecutiveErrorsToAbort - 1; i++) h.Record(TurnOutcome.Error);
            Ok(h, 1);
        }

        h.TurnsErrored.Should().Be(5 * (RunHealth.ConsecutiveErrorsToAbort - 1));
    }

    [Fact]
    public void One_flake_on_a_short_filtered_run_is_still_only_a_note()
    {
        // 1 in 12 is 8% — over the rate, under the floor. A banner here would be crying wolf on the
        // flake this harness already knows about, and a banner nobody trusts protects nothing.
        var h = new RunHealth();
        Ok(h, 11);
        h.Record(TurnOutcome.Error);

        h.ErrorRate.Should().BeGreaterThan(RunHealth.DegradedErrorRate);
        h.IsDegraded.Should().BeFalse();
    }

    [Fact]
    public void The_dead_endpoint_run_would_be_condemned()
    {
        // The shape of the real incident, scaled down: a long stretch of failures among successes.
        var h = new RunHealth();
        for (var i = 0; i < 30; i++) { Ok(h, 2); h.Record(TurnOutcome.Error); h.Record(TurnOutcome.Error); }

        h.IsDegraded.Should().BeTrue();
        h.ErrorRate.Should().BeApproximately(0.5, 0.01);
    }

    [Fact]
    public void Degraded_tracks_the_rate_not_the_count()
    {
        var h = new RunHealth();
        Ok(h, 500);
        h.Record(TurnOutcome.Error);

        h.TurnsErrored.Should().Be(1);
        h.IsDegraded.Should().BeFalse("one turn in 501 is well under the threshold");
    }

    [Fact]
    public void A_clean_run_reports_no_errors_and_is_not_degraded()
    {
        var h = new RunHealth();
        Ok(h, 100);

        h.TurnsRun.Should().Be(100);
        h.TurnsErrored.Should().Be(0);
        h.ErrorRate.Should().Be(0);
        h.IsDegraded.Should().BeFalse();
    }
}
