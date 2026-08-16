namespace TheKrystalShip.Kgsm.Assistant.Ports;

/// <summary>
/// Where the assistant records what it did that nothing else can see.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately not a log of what the assistant did.</b> Every mutation runs through kgsm-lib with
/// provenance attached, so the engine's own journal already records it, attributed to the person who
/// asked. A copy here would be a second answer able to disagree with the engine's.
/// </para>
/// <para>
/// What goes through this port is the opposite: <b>the turn that did not act</b>. A refusal, a
/// proposal nobody approved, and a claim of an action that never happened all leave the engine's
/// record empty — from its side nothing occurred — and so exist nowhere else on the host.
/// </para>
/// <para>
/// ⚠ <b>A port rather than a direct write, because this library is shared.</b> The CLI and the
/// benchmark compose the same graph as the resident service, and neither is a leaf: the CLI is a
/// one-shot with no residency to report, and an eval run would put hundreds of turn-quality lines in
/// the live journal. Only the service registers a real implementation, so the other two write nothing
/// by construction rather than by remembering to.
/// </para>
/// </remarks>
public interface IAssistantJournal
{
    /// <summary>
    /// Records that a reply described an action the turn never took, or a lookup it never made.
    /// </summary>
    /// <param name="check">Which check found it.</param>
    /// <param name="resolution">What was done about it.</param>
    /// <param name="net">Which net caught it.</param>
    /// <param name="conversationId">The conversation it happened in.</param>
    /// <remarks>
    /// ⚠ <b>Never give this the prompt or the reply.</b> The journal is readable by anything on the
    /// host that can open the directory; a transcript belongs to the person who spoke it.
    /// </remarks>
    void ClaimCorrected(
        ClaimCheck check, ClaimResolution resolution, ClaimNet net, string? conversationId);

    /// <summary>
    /// Records that somebody reached for an action their tier does not carry.
    /// </summary>
    /// <param name="tool">The tool that was refused.</param>
    /// <param name="instance">The instance it would have touched, when it named one.</param>
    /// <remarks>
    /// ⚠ <b>No reason is passed, because the gate does not know it.</b> It sees one boolean, false
    /// whether this host has actions switched off for everybody or this person's tier does not carry
    /// them — two very different facts for whoever reads the record. The host knows which, so the
    /// adapter decides; guessing here would file a config state as somebody exceeding their permissions.
    /// </remarks>
    /// <remarks>
    /// ⚠ Authorization only. The blast-radius refusals — too many staged commands, too many searches, a
    /// repeated lookup — are loop guards firing on ordinary model over-eagerness, not somebody reaching
    /// past their permissions, and recording them would bury the ones that matter.
    /// </remarks>
    void ActionDeclined(string tool, string? instance);

    /// <summary>
    /// Records that a mutation is staged and waiting on a person.
    /// </summary>
    /// <param name="kind">What kind of action, by name.</param>
    /// <param name="tool">The tool that staged it.</param>
    /// <param name="instance">The instance it would act on, when it names one.</param>
    /// <param name="expiresInSec">How long it stays redeemable.</param>
    /// <remarks>
    /// ⚠ <b>Never give this the handle.</b> The handle is the capability that redeems the action, and a
    /// journal readable by anything on the host is not where a capability goes.
    /// </remarks>
    void ActionProposed(string kind, string? tool, string? instance, long? expiresInSec);

    /// <summary>Records that a blueprint-authoring run began.</summary>
    /// <param name="blueprint">The blueprint being authored.</param>
    /// <param name="probe">The disposable instance the run installs to test its draft.</param>
    void BlueprintAuthoringStarted(string blueprint, string probe);

    /// <summary>Records that a blueprint-authoring run concluded, however it ended.</summary>
    /// <param name="blueprint">The blueprint being authored.</param>
    /// <param name="probe">The disposable instance the run tested with.</param>
    /// <param name="outcome">How it ended.</param>
    /// <param name="durationSec">How long the run took.</param>
    void BlueprintAuthored(
        string blueprint, string probe, AuthoringOutcome outcome, long? durationSec);
}

/// <summary>
/// The journal a host that is not a leaf writes to: nowhere.
/// </summary>
/// <remarks>
/// Registered by default so the graph composes with nothing configured, and so the CLI and the
/// benchmark — which compose it and are not leaves — record nothing without having to know that.
/// </remarks>
public sealed class NoAssistantJournal : IAssistantJournal
{
    public void ClaimCorrected(
        ClaimCheck check, ClaimResolution resolution, ClaimNet net, string? conversationId) { }

    public void ActionDeclined(string tool, string? instance) { }

    public void ActionProposed(string kind, string? tool, string? instance, long? expiresInSec) { }

    public void BlueprintAuthoringStarted(string blueprint, string probe) { }

    public void BlueprintAuthored(
        string blueprint, string probe, AuthoringOutcome outcome, long? durationSec) { }
}

/// <summary>Which integrity check found a reply wanting.</summary>
/// <remarks>
/// ⚠ An enum rather than the wire string, because this library must not depend on kgsm-lib — the
/// brain stays domain-pure and the adapter owns the one mapping onto the journal's vocabulary. That
/// also makes the mapping exhaustive: a member added here fails to compile until it is spelled.
/// </remarks>
public enum ClaimCheck
{
    /// <summary>The reply claimed an action on a turn that staged and ran nothing.</summary>
    UnbackedAction,

    /// <summary>The person asked for the web and the turn looked nothing up.</summary>
    UnsearchedWeb,
}

/// <summary>What was done about a reply that failed its check.</summary>
public enum ClaimResolution
{
    /// <summary>The turn was given another attempt, told what it had and had not done.</summary>
    RePrompted,

    /// <summary>A correction was appended and the reply left standing.</summary>
    Corrected,
}

/// <summary>
/// Which of the two nets caught a claim.
/// </summary>
/// <remarks>
/// They sit at different depths: the review runs where the turn can still be re-prompted, and the
/// outer net runs over the reply the turn ends on. Which one caught a given claim is the difference
/// between a model that can be talked out of it and one that cannot.
/// </remarks>
public enum ClaimNet
{
    /// <summary>The per-reply review, where a re-prompt is still possible.</summary>
    Review,

    /// <summary>The outer net over the finished reply.</summary>
    Outer,
}

/// <summary>Why an action was refused.</summary>
public enum DeclineReason
{
    /// <summary>The caller's tier does not carry the action.</summary>
    Authority,

    /// <summary>This host has actions turned off entirely.</summary>
    ActionsDisabled,
}

/// <summary>How a blueprint-authoring run concluded.</summary>
public enum AuthoringOutcome
{
    /// <summary>The draft installed, started, and was seen to be ready.</summary>
    Verified,

    /// <summary>A draft came back for a person to review, unverified.</summary>
    DraftReady,

    /// <summary>The run did not produce a usable blueprint.</summary>
    Failed,
}
