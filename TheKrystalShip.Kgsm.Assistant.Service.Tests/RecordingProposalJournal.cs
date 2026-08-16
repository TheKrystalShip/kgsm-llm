using TheKrystalShip.Kgsm.Assistant.Ports;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

/// <summary>The assistant's journal, kept in a list so a test can assert what a staging recorded.</summary>
public sealed class RecordingProposalJournal : IAssistantJournal
{
    /// <summary>One <c>assistant_action_proposed</c>.</summary>
    public sealed record Proposal(string Kind, string? Tool, string? Instance, long? ExpiresInSec);

    public List<Proposal> Proposals { get; } = [];

    public void ActionProposed(string kind, string? tool, string? instance, long? expiresInSec) =>
        Proposals.Add(new Proposal(kind, tool, instance, expiresInSec));

    public void ClaimCorrected(
        ClaimCheck check, ClaimResolution resolution, ClaimNet net, string? conversationId) { }

    public void ActionDeclined(string tool, string? instance) { }

    public void BlueprintAuthoringStarted(string blueprint, string probe) { }

    public void BlueprintAuthored(
        string blueprint, string probe, AuthoringOutcome outcome, long? durationSec) { }
}
