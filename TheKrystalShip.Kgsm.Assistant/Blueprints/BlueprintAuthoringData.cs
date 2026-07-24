namespace TheKrystalShip.Kgsm.Assistant.Blueprints;

/// <summary>
/// How a <c>create_blueprint</c> run concluded (toolbox-plan §"Pipeline"/step 11). Every value maps to
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
/// text (<see cref="Envelope.ToolResult{TData}.Summary"/>) carries the plain-language outcome and, on
/// <see cref="BlueprintAuthoringOutcome.Verified"/>, the one human-sized proof line.
/// </summary>
public sealed record BlueprintAuthoringData(
    BlueprintAuthoringOutcome Outcome,
    string Game,
    string? BlueprintName,
    IReadOnlyList<BlueprintFieldProvenance> Sourced,
    string? ProofLine,
    bool OfferInstance);
