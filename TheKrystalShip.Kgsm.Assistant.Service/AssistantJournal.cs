using System.Text.Json;

using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Service.Configuration;

using TheKrystalShip.Kgsm.Assistant.Infrastructure;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Events;
using TheKrystalShip.KGSM.Services;

namespace TheKrystalShip.Kgsm.Assistant.Service;

/// <summary>
/// What this leaf records about its own conduct.
/// </summary>
/// <remarks>
/// <para>
/// The write half of the assistant's journal. Separate from <see cref="LeafLifecycle"/> because the
/// two answer to different identities: a lifecycle line is this process reporting on itself with
/// nobody behind it, and every line here carries the person whose turn it was.
/// </para>
/// <para>
/// <b>Fire-and-forget on purpose.</b> The port is synchronous because every call site is on the path
/// of a turn that has already happened — a refusal already refused, a claim already corrected. Waiting
/// on a disk write to finish answering somebody would trade a fast reply for a record that is written
/// either way.
/// </para>
/// </remarks>
public sealed class AssistantJournal(
    IEventJournalWriter writer,
    IInvocationContext invocation,
    IOptionsMonitor<AssistantServiceOptions> options,
    ILogger<AssistantJournal> logger)
    : JournalRecorder(writer, logger), IAssistantJournal
{
    /// <summary>
    /// The producer id — this leaf's state directory's own name, which is what a reader scans for, so
    /// writer and readers agree on the location without either being told.
    /// </summary>
    public const string Producer = "kgsm-assistant";

    private readonly IInvocationContext _invocation = invocation;

    /// <summary>
    /// Whether this host allows actions at all, read fresh on every line.
    /// </summary>
    /// <remarks>
    /// A monitor rather than a snapshot: the switch is a live configuration value, and a refusal
    /// recorded minutes after it was flipped must say why it was refused now, not at startup.
    /// </remarks>
    private readonly Func<bool> _actionsEnabled = () => options.CurrentValue.ActionsEnabled;
    private readonly ILogger<AssistantJournal> _logger = logger;

    /// <summary>
    /// Null, because every event here is driven by a person.
    /// </summary>
    /// <remarks>
    /// ⚠ The base defaults to <c>system:kgsm-assistant</c>, which would be a fabricated author: these
    /// record what happened on somebody's turn, and attributing one to the daemon that carried it out
    /// states an actor it does not know. The real one comes off the ambient invocation, which is the
    /// same value the engine stamps on the actions this leaf performs — so a refusal and the action it
    /// refused are attributable to one person by the same string.
    /// </remarks>
    protected override string? DefaultActor => _invocation.Current?.Actor;

    /// <summary>The surface the person was using, or null when this leaf cannot tell.</summary>
    /// <remarks>
    /// ⚠ Never <c>system</c>. A turn always came from somewhere; a line written outside any invocation
    /// scope reports an honest null rather than claiming the daemon drove it.
    /// </remarks>
    protected override string? DefaultOrigin => _invocation.Current?.Origin;

    public void ClaimCorrected(
        ClaimCheck check, ClaimResolution resolution, ClaimNet net, string? conversationId) =>
        Write(AssistantEvents.ClaimCorrected, w =>
        {
            w.WriteString(AssistantEventFields.Check, Wire(check));
            w.WriteString(AssistantEventFields.Resolution, Wire(resolution));
            w.WriteString(AssistantEventFields.Net, Wire(net));
            WriteOptional(w, AssistantEventFields.ConversationId, conversationId);
        });

    public void ActionDeclined(string tool, string? instance) =>
        Write(AssistantEvents.ActionDeclined, w =>
        {
            w.WriteString(AssistantEventFields.Tool, tool);
            // ⚠ Decided here rather than at the gate, which sees one boolean covering both. A host with
            // actions switched off refuses everybody, which is a configuration state; a host with them on
            // refuses the person, which is somebody reaching past their tier. Filing the first as the
            // second would put a permanent config fact in the record as a stream of attempted overreach.
            w.WriteString(
                AssistantEventFields.DeclineReason,
                _actionsEnabled() ? AssistantDeclineReasons.Authority : AssistantDeclineReasons.ActionsDisabled);
            WriteOptional(w, AssistantEventFields.Instance, instance);
        });

    public void ActionProposed(string kind, string? tool, string? instance, long? expiresInSec) =>
        Write(AssistantEvents.ActionProposed, w =>
        {
            w.WriteString(AssistantEventFields.Kind, kind);
            WriteOptional(w, AssistantEventFields.Tool, tool);
            WriteOptional(w, AssistantEventFields.Instance, instance);
            if (expiresInSec is { } seconds)
                w.WriteNumber(AssistantEventFields.ExpiresInSec, seconds);
        });

    public void BlueprintAuthoringStarted(string blueprint, string probe) =>
        Write(AssistantEvents.BlueprintAuthoringStarted, w =>
        {
            w.WriteString("BlueprintName", blueprint);
            w.WriteString(AssistantEventFields.Probe, probe);
        });

    public void BlueprintAuthored(
        string blueprint, string probe, AuthoringOutcome outcome, long? durationSec) =>
        Write(AssistantEvents.BlueprintAuthored, w =>
        {
            w.WriteString("BlueprintName", blueprint);
            w.WriteString(AssistantEventFields.Probe, probe);
            w.WriteString(AssistantEventFields.AuthoringOutcome, Wire(outcome));
            if (durationSec is { } seconds)
                w.WriteNumber(AssistantEventFields.DurationSec, seconds);
        });

    /// <summary>
    /// The one place the brain's vocabulary becomes the journal's.
    /// </summary>
    /// <remarks>
    /// Switch expressions with no default arm on purpose: the brain names these as enums so this
    /// mapping is exhaustive, and a member added there fails <em>this</em> build until somebody decides
    /// what it is called on the wire. A default arm would quietly file it as something else.
    /// </remarks>
    internal static string Wire(ClaimCheck check) => check switch
    {
        ClaimCheck.UnbackedAction => AssistantClaimChecks.UnbackedAction,
        ClaimCheck.UnsearchedWeb => AssistantClaimChecks.UnsearchedWeb,
    };

    internal static string Wire(ClaimResolution resolution) => resolution switch
    {
        ClaimResolution.RePrompted => AssistantClaimResolutions.RePrompted,
        ClaimResolution.Corrected => AssistantClaimResolutions.Corrected,
    };

    internal static string Wire(ClaimNet net) => net switch
    {
        ClaimNet.Review => AssistantClaimNets.Review,
        ClaimNet.Outer => AssistantClaimNets.Outer,
    };

    internal static string Wire(AuthoringOutcome outcome) => outcome switch
    {
        AuthoringOutcome.Verified => AssistantAuthoringOutcomes.Verified,
        AuthoringOutcome.DraftReady => AssistantAuthoringOutcomes.DraftReady,
        AuthoringOutcome.Failed => AssistantAuthoringOutcomes.Failed,
    };

    /// <summary>
    /// Appends one line without making the caller wait for it.
    /// </summary>
    /// <remarks>
    /// The actor and origin are read <b>here</b>, on the calling flow, rather than inside the
    /// continuation: the invocation is an <c>AsyncLocal</c> scoped to the request, and a fire-and-forget
    /// continuation can outlive the scope that set it. Reading it late would attribute a line to
    /// whoever happened to be in scope, or to nobody.
    /// </remarks>
    private void Write(string eventType, Action<Utf8JsonWriter> payload)
    {
        string? actor = DefaultActor;
        string? origin = DefaultOrigin;

        _ = Task.Run(async () =>
        {
            try
            {
                await RecordAsync(eventType, payload, actor, origin).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The base already reports a failed write; this catches anything the composition of a
                // payload could throw, because an unobserved exception on a background task would take
                // the process down over a record.
                _logger.LogDebug(ex, "could not record {EventType}", eventType);
            }
        });
    }

    /// <summary>Writes a field, or omits it entirely when there is no value.</summary>
    /// <remarks>
    /// Omitted rather than written as an empty string: absent means "this event does not carry one",
    /// and an empty string is a third state a reader has to guess about.
    /// </remarks>
    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            writer.WriteString(name, value);
    }
}
