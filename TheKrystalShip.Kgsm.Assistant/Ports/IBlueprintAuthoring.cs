using TheKrystalShip.Kgsm.Assistant.Blueprints;
using TheKrystalShip.Kgsm.Assistant.Envelope;

namespace TheKrystalShip.Kgsm.Assistant.Ports;

/// <summary>
/// The <c>create_blueprint</c> capability (<c>assistant-blueprint-authoring-plan.md</c>): given a game
/// missing from the catalog, researches it, drafts and test-installs a native-Linux blueprint, verifies
/// it empirically (boots + listens), and keeps it only if verified — the whole pipeline behind ONE
/// model-facing tool. Implementations MUST NOT throw; a run that cannot proceed (disabled, infeasible,
/// unverified) is a normal <see cref="BlueprintAuthoringOutcome"/>, never an exception. The concrete
/// pipeline needs kgsm-lib's write-side blueprint/instance authorities, so — unlike most assistant
/// ports — its implementation lives in Infrastructure (<c>BlueprintAuthoringAggregator</c>), not here;
/// this port is what keeps the dispatcher decoupled from that dependency.
/// </summary>
public interface IBlueprintAuthoring
{
    /// <summary>The autonomous full run — research → draft → test-install → verify → repair → keep/stash,
    /// with no human in the loop (exhaustion ends at <see cref="BlueprintAuthoringOutcome.Failed"/>). The
    /// mandatory-review flow uses <see cref="DraftAsync"/> + <see cref="FinalizeAsync"/> instead; this
    /// entry is kept for callers that want the unattended pipeline.</summary>
    Task<ToolResult<BlueprintAuthoringData>> AuthorAsync(string game, CancellationToken cancellationToken = default);

    /// <summary>The draft half of the human-review flow (<c>assistant-blueprint-review-plan.md</c>):
    /// research → build, then return a <see cref="BlueprintAuthoringOutcome.DraftReady"/> card carrying the
    /// rendered YAML for the user to review and edit in the chat — NO test-install runs. A run that stops
    /// before a draft (disabled, already-present, infeasible, no launchable draft) returns that terminal
    /// outcome instead. The surface stages a Blueprint confirmation from the returned draft; the user's
    /// Save calls <see cref="FinalizeAsync"/>.</summary>
    Task<ToolResult<BlueprintAuthoringData>> DraftAsync(string game, CancellationToken cancellationToken = default);

    /// <summary>The finalize half: takes the (possibly user-edited) blueprint YAML back, re-validates it
    /// (structural parse + the <c>$instance_*</c>-only placeholder guard), then test-installs → verifies →
    /// bounded self-repairs → keeps/stashes. Stateless — everything needed is re-derived from
    /// <paramref name="editedYaml"/> (no authoring session is held between draft and finalize). When the
    /// repair loop exhausts, returns a recovery <see cref="BlueprintAuthoringOutcome.DraftReady"/> (the last
    /// draft + boot evidence) for the user to edit and save again, rather than a terminal failure. An edit
    /// that won't parse or trips the placeholder guard likewise comes back as an editable DraftReady with an
    /// explanation.</summary>
    Task<ToolResult<BlueprintAuthoringData>> FinalizeAsync(string game, string editedYaml, CancellationToken cancellationToken = default);

    /// <summary>Conversational refinement of an OPEN draft (before it's saved): takes the full revised YAML
    /// the model produced from the draft the user is reviewing, re-validates it (the same structural parse +
    /// <c>$instance_*</c>-only placeholder guard as <see cref="FinalizeAsync"/>), and returns a fresh
    /// <see cref="BlueprintAuthoringOutcome.DraftReady"/> card with the normalized YAML — NO test-install.
    /// This is what lets the assistant actually change a draft from chat instead of only producing the first
    /// one; the surface re-stages a Blueprint confirmation from it, exactly like the initial draft. An edit
    /// that won't parse or trips the guard comes back as an editable DraftReady with an explanation, never a
    /// hard failure.</summary>
    Task<ToolResult<BlueprintAuthoringData>> ReviseAsync(string revisedYaml, CancellationToken cancellationToken = default);
}

/// <summary>Default <see cref="IBlueprintAuthoring"/> for hosts that haven't enabled blueprint
/// authoring: every call reports it honestly as not configured, so embedding the assistant library
/// never breaks DI just because <c>BlueprintAuthoring:Enabled</c> is unset (false by default
/// everywhere). A host that wants the real pipeline calls <c>AddKgsmAdapters</c> with it enabled, which
/// registers the concrete aggregator that wins over this default.</summary>
internal sealed class DisabledBlueprintAuthoring : IBlueprintAuthoring
{
    public Task<ToolResult<BlueprintAuthoringData>> AuthorAsync(string game, CancellationToken cancellationToken = default) =>
        Task.FromResult(Disabled(game));

    public Task<ToolResult<BlueprintAuthoringData>> DraftAsync(string game, CancellationToken cancellationToken = default) =>
        Task.FromResult(Disabled(game));

    public Task<ToolResult<BlueprintAuthoringData>> FinalizeAsync(string game, string editedYaml, CancellationToken cancellationToken = default) =>
        Task.FromResult(Disabled(game));

    public Task<ToolResult<BlueprintAuthoringData>> ReviseAsync(string revisedYaml, CancellationToken cancellationToken = default) =>
        Task.FromResult(Disabled("that game"));

    private static ToolResult<BlueprintAuthoringData> Disabled(string game)
    {
        game = game.Trim();
        var summary = $"Automatic blueprint authoring isn't enabled on this host, so I can't research " +
                       $"and build a config for \"{game}\" myself.";
        var data = new BlueprintAuthoringData(
            BlueprintAuthoringOutcome.Disabled, game, null, [], null, Reason: summary, OfferInstance: false);
        return new ToolResult<BlueprintAuthoringData>(
            LlmTools.CreateBlueprint, Confidence.Confirmed, new ResultRef(ResourceKind.Blueprint, game), summary, data);
    }
}
