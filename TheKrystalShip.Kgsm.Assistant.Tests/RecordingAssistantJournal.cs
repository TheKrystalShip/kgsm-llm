using TheKrystalShip.Kgsm.Assistant.Ports;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// The assistant's journal, kept in a list so a test can assert what was recorded.
/// </summary>
/// <remarks>
/// A recording fake rather than a mock: what these events say is the thing under test — the check that
/// fired, whether the turn was re-prompted or corrected, and which net caught it — and reading that
/// off a list is clearer than a call-verification expression that spells the same tuple.
/// </remarks>
public sealed class RecordingAssistantJournal : IAssistantJournal
{
    /// <summary>One <c>assistant_claim_corrected</c>, as the brain reported it.</summary>
    public sealed record Claim(ClaimCheck Check, ClaimResolution Resolution, ClaimNet Net, string? ConversationId);

    /// <summary>One <c>assistant_action_declined</c>.</summary>
    public sealed record Decline(string Tool, string? Instance);

    public List<Claim> Claims { get; } = [];

    public List<Decline> Declines { get; } = [];

    public void ClaimCorrected(
        ClaimCheck check, ClaimResolution resolution, ClaimNet net, string? conversationId) =>
        Claims.Add(new Claim(check, resolution, net, conversationId));

    public void ActionDeclined(string tool, string? instance) =>
        Declines.Add(new Decline(tool, instance));

    public void ActionProposed(string kind, string? tool, string? instance, long? expiresInSec) { }

    public void BlueprintAuthoringStarted(string blueprint, string probe) { }

    public void BlueprintAuthored(
        string blueprint, string probe, AuthoringOutcome outcome, long? durationSec) { }
}
