using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Status;

/// <summary>
/// What is known about a confirmed operation once it has run. The engine's exit code answers
/// "was the request accepted", which for a lifecycle verb is not the same question as "is the
/// server running" — a native start returns as soon as the watchdog accepts the spawn. A verdict
/// separates those, so no surface has to guess which one it was told.
/// </summary>
public enum ConfirmVerdict
{
    /// <summary>The operation ran and its run-state postcondition was observed.</summary>
    Settled,

    /// <summary>
    /// The operation ran and the engine reported success, and it has no run-state postcondition
    /// to observe (an update, a backup, a config write). The engine's own success is the whole of
    /// what is known — which is honest, and deliberately not called <see cref="Settled"/>.
    /// </summary>
    Accepted,

    /// <summary>
    /// The operation ran, but the run state had still not reached its postcondition when the
    /// observation window closed. <see cref="ConfirmOutcome.ObservedState"/> carries what was
    /// actually seen. Not a failure — the operation may still land — but never a success.
    /// </summary>
    NotSettled,

    /// <summary>
    /// The operation ran and the end state could not be read at all, so the outcome is genuinely
    /// unknown. Never collapsed into <see cref="NotSettled"/>: "we looked and it wasn't running
    /// yet" and "we could not look" are different facts.
    /// </summary>
    Unknown,

    /// <summary>The operation itself reported failure.</summary>
    Failed,

    /// <summary>
    /// Nothing ran — the caller was not authorized, the target no longer exists, or the staged
    /// payload was unusable.
    /// </summary>
    Refused,
}

/// <summary>
/// The outcome of a confirmed operation: the verdict, a human-readable line, and — for a verb
/// with a run-state postcondition — the state actually observed.
/// <para>
/// This replaces a bare outcome string on the confirm path. A string cannot distinguish "it is
/// running" from "the engine accepted the request", and every surface that rendered one had to
/// decide for itself which had happened; they did not all decide the same way.
/// </para>
/// </summary>
/// <param name="Verdict">What is known about the operation.</param>
/// <param name="Summary">The human-readable outcome line. Never claims more than the verdict.</param>
/// <param name="Verb">The imperative verb, when the outcome came from a known operation.</param>
/// <param name="Instance">The instance the operation targeted, when there was one.</param>
/// <param name="ObservedState">
/// The run state measured after the operation — set for the verbs that have a run-state
/// postcondition, absent for the rest. <see cref="ServerRunState.Unknown"/> means the read
/// failed, never "not running".
/// </param>
/// <param name="Reason">Why the state could not be read, or why the operation failed.</param>
public sealed record ConfirmOutcome(
    ConfirmVerdict Verdict,
    string Summary,
    string? Verb = null,
    string? Instance = null,
    ServerRunState? ObservedState = null,
    string? Reason = null)
{
    /// <summary>
    /// Whether this outcome may be presented as a success. True only when the postcondition was
    /// observed, or when the engine reported success for a verb that has none — a
    /// <see cref="ConfirmVerdict.NotSettled"/> or <see cref="ConfirmVerdict.Unknown"/> outcome is
    /// deliberately not a success.
    /// </summary>
    public bool Ok => Verdict is ConfirmVerdict.Settled or ConfirmVerdict.Accepted;

    public static ConfirmOutcome Refused(string summary) =>
        new(ConfirmVerdict.Refused, summary);

    public static ConfirmOutcome Failed(string summary, string verb, string instance, string? reason = null) =>
        new(ConfirmVerdict.Failed, summary, verb, instance, Reason: reason);

    public static ConfirmOutcome Accepted(string summary, string verb, string? instance = null) =>
        new(ConfirmVerdict.Accepted, summary, verb, instance);
}

/// <summary>
/// How long to watch for a run-state postcondition, and how often to look. Parameterised rather
/// than fixed so a test can settle in milliseconds instead of waiting out the real window.
/// </summary>
/// <param name="Timeout">The ceiling on the whole observation, not an expected duration.</param>
/// <param name="PollInterval">The gap between reads.</param>
public sealed record SettlementTiming(TimeSpan Timeout, TimeSpan PollInterval)
{
    /// <summary>
    /// 90 seconds at 1-second reads. The window is a ceiling, not an expectation: the run-state
    /// façade reports on the process, which normally flips within a second or two, so the common
    /// case returns on the first read and the ceiling only matters when something is wrong. It is
    /// generous because a restart is a stop and a start, and a game that saves on shutdown makes
    /// the stop the slow half.
    /// </summary>
    public static readonly SettlementTiming Default =
        new(TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(1));
}

/// <summary>
/// Runs a confirmed single-instance command and then <em>observes</em> whether it did what it
/// said, instead of reporting the engine's exit code as the outcome.
/// <para>
/// The observation source is <see cref="IServerOperations.GetFleetStatusAsync"/> — the same
/// measured-or-unavailable read the fleet card is built from, so the confirm path and the status
/// card can never disagree about what a server is doing. It is used rather than a per-instance
/// liveness check because that check collapses "not running" and "could not read" into one
/// boolean, which is exactly the distinction a verdict has to keep.
/// </para>
/// <para>
/// What is observed is the engine's run state — the process is up — not that the game inside it
/// is ready to accept players. <see cref="ConfirmVerdict.Settled"/> claims the former and nothing
/// more.
/// </para>
/// </summary>
public static class CommandSettlement
{
    /// <summary>
    /// The run state a verb is expected to reach, or null when the verb has no run-state
    /// postcondition to observe (its outcome is the engine's own success).
    /// </summary>
    public static bool? ExpectedRunning(ConfirmationKind kind) => kind switch
    {
        ConfirmationKind.Start or ConfirmationKind.Restart => true,
        ConfirmationKind.Stop => false,
        _ => null,
    };

    /// <summary>
    /// Executes <paramref name="operation"/> and settles the result against the observed run
    /// state. A verb with no run-state postcondition returns <see cref="ConfirmVerdict.Accepted"/>
    /// on the engine's success without inventing an observation.
    /// </summary>
    public static async Task<ConfirmOutcome> RunAndSettleAsync(
        IServerOperations operations,
        ConfirmationKind kind,
        string instance,
        Func<string, CancellationToken, Task<Result>> operation,
        SettlementTiming? timing = null,
        ITurnProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        var verb = ConfirmationKinds.Verb(kind);

        var ran = await operation(instance, cancellationToken);
        if (ran.IsFailure)
            return ConfirmOutcome.Failed(
                $"Could not {verb} '{instance}': {ran.Error ?? "unknown error"}.",
                verb, instance, ran.Error);

        var expected = ExpectedRunning(kind);
        if (expected is null)
            return ConfirmOutcome.Accepted(
                $"'{instance}' has been {ConfirmationKinds.PastTense(kind)}.", verb, instance);

        return await SettleAsync(
            operations, kind, instance, expected.Value,
            timing ?? SettlementTiming.Default, progress, cancellationToken);
    }

    /// <summary>
    /// Watches the instance's run state until it reaches <paramref name="expectedRunning"/> or the
    /// window closes. The first read happens immediately, so an operation that has already landed
    /// costs one status read and no delay.
    /// </summary>
    public static async Task<ConfirmOutcome> SettleAsync(
        IServerOperations operations,
        ConfirmationKind kind,
        string instance,
        bool expectedRunning,
        SettlementTiming timing,
        ITurnProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        var verb = ConfirmationKinds.Verb(kind);
        var deadline = DateTimeOffset.UtcNow + timing.Timeout;

        ServerRunState lastState = ServerRunState.Unknown;
        string? lastReason = null;
        var narrated = false;

        while (true)
        {
            (lastState, lastReason) = await ReadRunStateAsync(operations, instance, cancellationToken);

            if (lastState is ServerRunState.Running or ServerRunState.Stopped)
            {
                var running = lastState == ServerRunState.Running;
                if (running == expectedRunning)
                    return new ConfirmOutcome(
                        ConfirmVerdict.Settled,
                        $"'{instance}' has been {ConfirmationKinds.PastTense(kind)}.",
                        verb, instance, lastState);
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                break;

            // Narrate only once we are actually waiting. The common case settles on the first read, and a
            // step announcing a wait that never happened would be narration of nothing.
            if (!narrated)
            {
                narrated = true;
                progress?.Report(
                    LlmTools.ServerCommand, "settling",
                    expectedRunning
                        ? $"Waiting for {instance} to come up…"
                        : $"Waiting for {instance} to shut down…");
            }

            var wait = remaining < timing.PollInterval ? remaining : timing.PollInterval;
            await Task.Delay(wait, cancellationToken);
        }

        // The window closed. Report what was actually last seen — a read that failed is Unknown,
        // never a "still stopped" that would read as a measured fact.
        if (lastState == ServerRunState.Unknown)
            return new ConfirmOutcome(
                ConfirmVerdict.Unknown,
                $"Ran {verb} on '{instance}', but its state could not be read, so whether it "
                + $"{ConfirmationKinds.PastTense(kind)} is unknown.",
                verb, instance, ServerRunState.Unknown, lastReason);

        return new ConfirmOutcome(
            ConfirmVerdict.NotSettled,
            $"Ran {verb} on '{instance}', but it is still "
            + (lastState == ServerRunState.Running ? "running" : "stopped")
            + $" after {(int)timing.Timeout.TotalSeconds}s.",
            verb, instance, lastState);
    }

    /// <summary>
    /// Reads one instance's run state off the fleet status, preserving the
    /// measured-vs-unavailable distinction. A failed fleet read, an instance missing from it, and
    /// an entry the engine could not read all map to <see cref="ServerRunState.Unknown"/> with a
    /// reason — never to a fabricated "stopped".
    /// </summary>
    private static async Task<(ServerRunState State, string? Reason)> ReadRunStateAsync(
        IServerOperations operations, string instance, CancellationToken cancellationToken)
    {
        var fleet = await operations.GetFleetStatusAsync(cancellationToken);
        if (fleet.IsFailure)
            return (ServerRunState.Unknown, fleet.Error ?? "the status read failed");

        var entry = fleet.Value!.FirstOrDefault(
            e => string.Equals(e.Instance, instance, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            return (ServerRunState.Unknown, "it is no longer listed");

        if (entry.Availability != FleetStatusAvailability.Read || entry.Running is null)
            return (ServerRunState.Unknown, entry.Reason ?? "its status could not be read");

        return (entry.Running.Value ? ServerRunState.Running : ServerRunState.Stopped, null);
    }
}
