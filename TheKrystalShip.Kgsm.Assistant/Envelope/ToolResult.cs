using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Envelope;

/// <summary>
/// Trust in an <em>inference</em> a tool result asserts (toolbox-plan §5). Orthogonal
/// to <see cref="CheckState"/> (a check's pass/fail judgment) and to a field's
/// measured-vs-unknown presence: a fact can be measured while the verdict is only
/// <see cref="Possible"/>, and vice versa. A purely deterministic read of measured
/// facts (like the health check) is <see cref="Confirmed"/>.
/// </summary>
public enum Confidence
{
    /// <summary>Measured facts / a deterministic conclusion — no guessing.</summary>
    Confirmed,

    /// <summary>A well-supported inference.</summary>
    Likely,

    /// <summary>A weak correlation — stated as such, never dressed up as a cause.</summary>
    Possible,
}

/// <summary>
/// The visual/semantic weight a surface renders for a result or a single check
/// (toolbox-plan §5). Distinct from <see cref="CheckState"/>: a check can be
/// <see cref="CheckState.Pass"/> yet carry <see cref="Severity.Info"/> (e.g. a
/// server that is deliberately stopped).
/// </summary>
public enum Severity
{
    /// <summary>Neutral information.</summary>
    Info,

    /// <summary>A good/healthy outcome.</summary>
    Success,

    /// <summary>Something to watch.</summary>
    Warn,

    /// <summary>Something wrong / urgent.</summary>
    Danger,

    /// <summary>An available update.</summary>
    Update,
}

/// <summary>
/// The judgment of one aggregator check (toolbox-plan §3.4/§5). <see cref="Skip"/>
/// means the check was not assessed (its source was unavailable or not applicable) —
/// it is NOT a pass, and must never be collapsed into one (the "never fabricate"
/// rule). An aggregate over checks ignores <see cref="Skip"/> when ranking severity.
/// </summary>
public enum CheckState
{
    /// <summary>The check passed.</summary>
    Pass,

    /// <summary>The check passed but flagged something worth attention.</summary>
    Warn,

    /// <summary>The check failed.</summary>
    Fail,

    /// <summary>The check was not assessed — no judgment rendered (source unavailable / N/A).</summary>
    Skip,
}

/// <summary>The kind of resource a <see cref="ResultRef"/> points at (toolbox-plan §5).</summary>
public enum ResourceKind
{
    /// <summary>A game-server instance.</summary>
    Server,

    /// <summary>The host machine.</summary>
    Host,

    /// <summary>The audit/event log.</summary>
    Audit,

    /// <summary>A metrics stream.</summary>
    Metrics,

    /// <summary>A knowledge search (the operator's indexed docs / the web) — the query is its id.</summary>
    Search,

    /// <summary>An instance's host-firewall network picture (the <c>get_network</c> subject).</summary>
    Network,
}

/// <summary>
/// A typed reference to a resource a result is about or links to (toolbox-plan §5's
/// <c>Ref</c>). A surface builds its route/URL from this; the model never sees it.
/// </summary>
/// <param name="Resource">The kind of resource.</param>
/// <param name="Id">Its identifier (e.g. the instance name).</param>
/// <param name="Section">Optional sub-section within the resource.</param>
public sealed record ResultRef(ResourceKind Resource, string Id, string? Section = null);

/// <summary>
/// The central, versioned tool-result envelope shared by every aggregator and read
/// (toolbox-plan §5/§6). It serves two readers from one shape: the <b>model</b> gets
/// <see cref="Summary"/> (its grounding text — §3.6), and a <b>surface</b> gets the
/// structured <see cref="Data"/> card. The model never sees <see cref="Data"/>.
/// <para>
/// This lives in the assistant library (the KGSM domain layer), not the generic LLM
/// package, which stays domain-agnostic. Today the dispatcher returns only
/// <see cref="Summary"/>; <see cref="Data"/> is built + tested and transported once a
/// surface (the web card) consumes it.
/// </para>
/// </summary>
/// <typeparam name="TData">The card payload type for the producing tool.</typeparam>
/// <param name="Tool">The producing tool's name (also the surface's card kind).</param>
/// <param name="Confidence">Trust in the result's conclusion.</param>
/// <param name="Subject">What the result is about.</param>
/// <param name="Summary">The model's grounding text — authored by the deterministic layer.</param>
/// <param name="Data">The structured card payload (surface-only).</param>
/// <param name="Links">Optional related references for the surface to route to.</param>
public sealed record ToolResult<TData>(
    Tool Tool,
    Confidence Confidence,
    ResultRef Subject,
    string Summary,
    TData Data,
    IReadOnlyList<ResultRef>? Links = null);
