using TheKrystalShip.Kgsm.Assistant.Blueprints;

namespace TheKrystalShip.Kgsm.Assistant.Ports;

/// <summary>
/// One <c>create_blueprint</c> attempt, recorded for admin-only, async-after-the-fact review (plan §"Two
/// surfaces") — never shown to the end user. Written on every non-<see cref="BlueprintAuthoringOutcome.AlreadyExists"/>
/// outcome that didn't end in <see cref="BlueprintAuthoringOutcome.Verified"/>, so a failed/infeasible
/// attempt is never silently lost. <see cref="DraftYaml"/> is the rendered YAML the pipeline tried (or
/// null if it never got that far — e.g. an infeasibility stop); <see cref="VerifyLog"/> is the
/// step-by-step trace (persist/readback/install/verify attempts) for diagnosing why it didn't verify.
/// </summary>
public sealed record BlueprintAttemptRecord(
    string Game,
    string? BlueprintName,
    DateTimeOffset AttemptedAt,
    BlueprintAuthoringOutcome Outcome,
    string Reason,
    string? DraftYaml,
    IReadOnlyList<BlueprintFieldProvenance> Provenance,
    IReadOnlyList<string> VerifyLog);

/// <summary>
/// The admin "attempted" stash (plan step 11 / "Two surfaces"): persists a <see cref="BlueprintAttemptRecord"/>
/// for later, out-of-band review. Never throws — a store that can't write logs and moves on, since a
/// stash failure must not turn an honest "couldn't do this one" into an error for the user.
/// </summary>
public interface IBlueprintAttemptStore
{
    Task RecordAsync(BlueprintAttemptRecord record, CancellationToken cancellationToken = default);
}

/// <summary>Default <see cref="IBlueprintAttemptStore"/> for hosts that haven't configured a stash
/// directory: records are dropped (logged at Debug, never thrown) rather than breaking DI. A host that
/// wants real records calls <c>AddKgsmAdapters</c> with <c>BlueprintAuthoring:StashDir</c> set, which
/// registers a concrete filesystem-backed store that wins over this default.</summary>
internal sealed class NullBlueprintAttemptStore : IBlueprintAttemptStore
{
    public Task RecordAsync(BlueprintAttemptRecord record, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
