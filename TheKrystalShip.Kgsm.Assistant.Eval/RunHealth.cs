using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Eval;

/// <summary>
/// Raised when the run stops because the model endpoint has stopped answering. Distinct from any
/// scoring outcome: a run that ends this way produced no measurement at all, and the caller must not
/// present one.
/// </summary>
internal sealed class EvalEndpointDownException(string message) : Exception(message);

/// <summary>
/// Watches how many turns are failing outright, so a dead model endpoint cannot be mistaken for a
/// model behaving badly.
/// <para>
/// The two look nothing alike once you know to check — an endpoint that has gone away produces
/// errored turns with empty replies and NO tool calls, so scores collapse while the work done
/// collapses with them. But a scorecard renders the same either way: every check simply fails, which
/// reads exactly like a catastrophic regression. That is the failure this guards, and the reason it
/// aborts rather than annotating: forty minutes spent measuring an endpoint that is not there buys
/// nothing, and the number at the end is worse than no number because it invites a conclusion.
/// </para>
/// <para>
/// One errored turn is NOT the signal. A local model occasionally returns an empty reply after tool
/// calls — a known flake for more than one model on this host — so a single failure is noise and a
/// run of them is the endpoint. Consecutive failures are therefore what trips the abort, while the
/// overall rate is carried into the result so a partially degraded run is reported as degraded
/// instead of quietly scoring low.
/// </para>
/// </summary>
internal sealed class RunHealth
{
    /// <summary>Consecutive errored turns that mean the endpoint is gone rather than the model
    /// having a bad turn. Small, because the cost of continuing is the rest of the run.</summary>
    public const int ConsecutiveErrorsToAbort = 5;

    /// <summary>Above this share of errored turns a run is reported as degraded — its score reflects
    /// an endpoint that was partly unavailable, whatever else it also reflects.</summary>
    public const double DegradedErrorRate = 0.02;

    /// <summary>
    /// Errors needed before the rate is allowed to condemn a run. A rate alone is misleading on a
    /// short one: a single known flake is 8% of a twelve-turn <c>--filter</c> run and would raise a
    /// banner saying the score is untrustworthy, which trains the reader to scroll past the banner.
    /// The warning is worth having only where it stays rare.
    /// </summary>
    public const int MinErrorsToDegrade = 3;

    private int _consecutive;

    public int TurnsRun { get; private set; }
    public int TurnsErrored { get; private set; }

    public double ErrorRate => TurnsRun == 0 ? 0 : (double)TurnsErrored / TurnsRun;
    public bool IsDegraded => TurnsErrored >= MinErrorsToDegrade && ErrorRate > DegradedErrorRate;

    /// <summary>
    /// Records one turn's outcome, throwing <see cref="EvalEndpointDownException"/> once enough
    /// have failed in a row. Called for every turn, including the steps of a multi-turn case.
    /// </summary>
    public void Record(TurnOutcome outcome)
    {
        TurnsRun++;

        if (outcome != TurnOutcome.Error)
        {
            _consecutive = 0;
            return;
        }

        TurnsErrored++;
        _consecutive++;

        if (_consecutive >= ConsecutiveErrorsToAbort)
            throw new EvalEndpointDownException(
                $"{_consecutive} turns in a row failed outright — the model endpoint is not answering. "
                + "Nothing measured here is about the assistant, so the run stopped instead of "
                + "producing a score that looks like a regression. Check that Ollama is up and "
                + "serving the model, then run again.");
    }
}
