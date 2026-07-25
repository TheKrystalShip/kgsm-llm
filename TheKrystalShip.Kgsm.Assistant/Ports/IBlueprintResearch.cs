namespace TheKrystalShip.Kgsm.Assistant.Ports;

/// <summary>
/// Whether research concluded a game can go through the native-Linux authoring pipeline
/// (<c>assistant-blueprint-authoring-plan.md</c>, "Scope (locked)"). Container/Wine servers are out of
/// scope for v1 — a game whose only self-host path is one of those reads
/// <see cref="NoNativeLinuxServer"/>, the same honest stop as a game with no dedicated server at all.
/// </summary>
public enum BlueprintFeasibility
{
    /// <summary>A native-Linux dedicated server was found — the authoring pipeline may proceed.</summary>
    Feasible,

    /// <summary>Research found the game is not self-hostable at all (no dedicated/community server exists).</summary>
    NotSelfHostable,

    /// <summary>A dedicated server exists but not for native Linux (Windows-only, or container/Wine-only —
    /// both out of scope for v1).</summary>
    NoNativeLinuxServer,

    /// <summary>A native-Linux server exists, but its files download only through a Steam account that
    /// OWNS the game (not the anonymous SteamCMD login the autonomous test-install uses). The blueprint
    /// can't be empirically verified without those credentials, so authoring stops and the requirement is
    /// surfaced honestly rather than failing opaquely at the download.</summary>
    RequiresSteamAccount,

    /// <summary>Research turned up nothing usable (no sources, or nothing conclusive in what was fetched).
    /// Treated the same as infeasible — the pipeline never proceeds on a guess.</summary>
    Inconclusive,
}

/// <summary>One extracted blueprint field, tagged with the page it came from — the provenance unit the
/// draft step consumes 1:1 into <see cref="Blueprints.BlueprintFieldProvenance"/>. A field research
/// could not confidently source is simply absent from <see cref="BlueprintResearchFindings.Fields"/>
/// (never present with a guessed value).</summary>
public sealed record BlueprintResearchField(string Name, string Value, string SourceUrl);

/// <summary>
/// What one research pass produced: the feasibility verdict, whatever fields were confidently sourced,
/// every page URL consulted (for the admin stash even when nothing was extracted), and a short
/// human-readable narrative of the pass (also for the stash — not shown to the end user).
/// </summary>
public sealed record BlueprintResearchFindings(
    BlueprintFeasibility Feasibility,
    string? DisplayName,
    IReadOnlyList<BlueprintResearchField> Fields,
    IReadOnlyList<string> SourceUrls,
    string Narrative);

/// <summary>
/// The research step of <c>create_blueprint</c> (plan step 2): finds and reads sources for one game and
/// extracts candidate native-blueprint fields, each tagged with its source. A clean seam over
/// <see cref="IWebSearch"/> (find sources) and <see cref="IWebFetch"/> (read them), then field extraction
/// that prefers model synthesis (<see cref="IBlueprintSynthesizer"/> — reads the pages in context to pick
/// the authoritative native launch) and falls back to a deterministic regex extractor when no model is
/// wired or synthesis can't source the required field. A field research is not confident about is left
/// out, never fabricated — and the whole pipeline's empirical boot+listen verification is the backstop
/// against a synthesized value that doesn't actually run.
/// </summary>
public interface IBlueprintResearch
{
    Task<BlueprintResearchFindings> ResearchAsync(string game, CancellationToken cancellationToken = default);
}
