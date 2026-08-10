namespace TheKrystalShip.Kgsm.Assistant.Blueprints;

/// <summary>
/// How a <c>create_blueprint</c> run concluded . Every value maps to
/// one honest outcome the tool reports — never a fabricated "it worked" or a silent partial state.
/// </summary>
public enum BlueprintAuthoringOutcome
{
    /// <summary>Automatic authoring is not enabled on this host — the pipeline never ran.</summary>
    Disabled,

    /// <summary>The game already has a blueprint in the catalog — nothing was done (the existence guard).</summary>
    AlreadyExists,

    /// <summary>Research found the game is not self-hostable, has no native-Linux server, or turned up
    /// nothing usable — an honest stop before any draft was written.</summary>
    NotFeasible,

    /// <summary>A draft was built and test-installed, but never verified booting + listening within the
    /// self-repair bound — the catalog stays clean (the draft was removed) and the attempt is stashed.</summary>
    Failed,

    /// <summary>The draft booted and listened on the host — kept in the catalog.</summary>
    Verified,

    /// <summary>A draft is rendered and awaiting the user's in-chat review before it is test-installed —
    /// the human-review checkpoint (<c>assistant-blueprint-review-plan.md</c>). Also the recovery surface
    /// when the autonomous repair loop exhausts: the last draft plus its boot evidence is returned for the
    /// user to edit rather than stopping at <see cref="Failed"/>. Nothing has been (or will be) persisted
    /// to the catalog on this outcome — the draft only rides in the card, to come back on the finalize
    /// confirmation.</summary>
    DraftReady,
}

/// <summary>One sourced (or deliberately unsourced) blueprint field — the provenance the research step
/// tags every extracted value with. <see cref="Value"/> is <see langword="null"/> for a field that was
/// never confidently sourced (never a fabricated placeholder); <see cref="SourceUrl"/> is the page the
/// value came from, null only when <see cref="Value"/> is also null.</summary>
public sealed record BlueprintFieldProvenance(string Field, string? Value, string? SourceUrl);

/// <summary>
/// The <c>create_blueprint</c> tool's structured card payload (the surface half of its
/// <see cref="Envelope.ToolResult{TData}"/>). <see cref="Sourced"/> is the full provenance trail (every
/// field the research step considered, sourced or not) — an admin-facing detail; the model's grounding
/// text (<see cref="Envelope.ToolResult{TData}.Summary"/>) carries the same plain-language outcome as
/// <see cref="ProofLine"/>/<see cref="Reason"/>, worded for the chat transcript rather than a card.
/// <see cref="Game"/> is the user-facing display name (the raw argument the model resolved this run
/// against); <see cref="BlueprintName"/> is the canonical slug, populated once the game is confirmed
/// installable (<see cref="BlueprintAuthoringOutcome.AlreadyExists"/> or
/// <see cref="BlueprintAuthoringOutcome.Verified"/>) — the same id the envelope's
/// <see cref="Envelope.ToolResult{TData}.Subject"/> carries from the point the slug is known, and what
/// the web install handoff sends as the blueprint name on <c>POST /servers</c>.
/// </summary>
/// <param name="Outcome">How the run concluded — the discriminator a card switches its rendering on.</param>
/// <param name="Game">The user-facing display name — the raw argument the model resolved this run against.</param>
/// <param name="BlueprintName">
/// The canonical slug, populated once the game is confirmed installable
/// (<see cref="BlueprintAuthoringOutcome.AlreadyExists"/> or <see cref="BlueprintAuthoringOutcome.Verified"/>);
/// null otherwise.
/// </param>
/// <param name="Sourced">The full provenance trail (every field the research step considered, sourced or not) — admin-facing.</param>
/// <param name="ProofLine">
/// The one human-sized "how I know it works" line on <see cref="BlueprintAuthoringOutcome.Verified"/>
/// (e.g. "it booted and is listening on its configured port") — never fabricated; null for every other
/// outcome (there is nothing true to say yet).
/// </param>
/// <param name="Reason">
/// The honest "why not" for every outcome that ISN'T <see cref="BlueprintAuthoringOutcome.Verified"/> or
/// <see cref="BlueprintAuthoringOutcome.AlreadyExists"/> (both of which need no excuse) — the same text
/// carried on the envelope's <see cref="Envelope.ToolResult{TData}.Summary"/>, exposed here as its own
/// field so a structured card can read it without parsing prose. Null on success.
/// </param>
/// <param name="OfferInstance">
/// Whether the game is now installable, so the surface can offer the "want me to make you a server?"
/// handoff — true for <see cref="BlueprintAuthoringOutcome.Verified"/> and
/// <see cref="BlueprintAuthoringOutcome.AlreadyExists"/>, false otherwise.
/// </param>
/// <param name="DraftYaml">
/// On <see cref="BlueprintAuthoringOutcome.DraftReady"/> only: the rendered native blueprint YAML the
/// user reviews and edits in the chat before it is test-installed (and the recovery draft returned when
/// the autonomous repair loop exhausts). Null on every other outcome — there is nothing to edit. The
/// surface stages a Blueprint confirmation carrying this text; the user's Save sends the (possibly edited)
/// YAML back to finalize.
/// </param>
/// <param name="Evidence">
/// On a recovery <see cref="BlueprintAuthoringOutcome.DraftReady"/> (the repair loop exhausted): a short
/// excerpt of the real boot log / install evidence, so the user sees WHY it didn't boot before editing.
/// Null on the initial pre-test draft (nothing has run yet) and on every non-DraftReady outcome.
/// </param>
/// <param name="Editable">
/// True only on <see cref="BlueprintAuthoringOutcome.DraftReady"/> — the signal for the surface to render
/// the editable Monaco card with Save/Restore. False on every terminal outcome.
/// </param>
public sealed record BlueprintAuthoringData(
    BlueprintAuthoringOutcome Outcome,
    string Game,
    string? BlueprintName,
    IReadOnlyList<BlueprintFieldProvenance> Sourced,
    string? ProofLine,
    string? Reason,
    bool OfferInstance,
    string? DraftYaml = null,
    string? Evidence = null,
    bool Editable = false);
