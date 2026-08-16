using TheKrystalShip.Kgsm.Assistant.Ports;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Tests;

/// <summary>
/// The assistant's journal, kept in lists so a test can assert the bracket around an authoring run.
/// </summary>
public sealed class RecordingAuthoringJournal : IAssistantJournal
{
    /// <summary>One <c>assistant_blueprint_authoring_started</c>.</summary>
    public sealed record Started(string Blueprint, string Probe);

    /// <summary>One <c>assistant_blueprint_authored</c>.</summary>
    public sealed record Authored(string Blueprint, string Probe, AuthoringOutcome Outcome, long? DurationSec);

    public List<Started> Starts { get; } = [];

    public List<Authored> Conclusions { get; } = [];

    public void BlueprintAuthoringStarted(string blueprint, string probe) =>
        Starts.Add(new Started(blueprint, probe));

    public void BlueprintAuthored(
        string blueprint, string probe, AuthoringOutcome outcome, long? durationSec) =>
        Conclusions.Add(new Authored(blueprint, probe, outcome, durationSec));

    public void ClaimCorrected(
        ClaimCheck check, ClaimResolution resolution, ClaimNet net, string? conversationId) { }

    public void ActionDeclined(string tool, string? instance) { }

    public void ActionProposed(string kind, string? tool, string? instance, long? expiresInSec) { }
}
