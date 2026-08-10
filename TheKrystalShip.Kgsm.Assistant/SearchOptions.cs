using TheKrystalShip.KGSM.LeafConfig;

namespace TheKrystalShip.Kgsm.Assistant;

/// <summary>
/// Tuning + availability for the unified <c>search</c> tool. The operator-tunable
/// thresholds bind from the "Rag" config section (the same section <c>RagOptions</c>/<c>RagEmbeddingOptions</c>
/// read — each picks up its own keys). The two <c>*Enabled</c> flags are COMPUTED by the host wiring, not
/// read from config: Infrastructure knows both whether RAG is on and whether a web provider is configured,
/// so it decides — once, at composition — whether <c>search</c> is offered at all (a tool with no
/// real source behind it is omitted, never shown as a dead option).
/// </summary>
[LeafSection(Section)]
public sealed class SearchOptions
{
    public const string Section = "Rag";

    /// <summary>
    /// Cosine floor at which a LOCAL hit is "good enough" to answer without falling back to the web.
    /// Deliberately distinct from <c>RagOptions.MinScore</c> (the retrieval hard floor, kept permissive
    /// at 0 so this aggregator sees the real top score): below this, a web lookup is preferred. Tuning
    /// this is Phase 5's job; 0.35 is a starting guess.
    /// </summary>
    /// <panel>How good the best local passage has to be for the assistant to trust it. Below this it
    /// searches the web instead.</panel>
    [LeafField("ragLocalMinScore", "Fall-back-to-web threshold", Group = "rag", Min = 0, Max = 1,
        DependsOn = "ragEnabled")]
    public double LocalMinScore { get; set; } = 0.35;

    /// <summary>
    /// Cap on the grounding text injected from local chunks, to protect the lean model's context window.
    /// At least the single strongest chunk is always included. Web results are already few and short, so
    /// this targets the local path.
    /// </summary>
    /// <panel>How much retrieved text the model is given at once.</panel>
    [LeafField("ragMaxContextChars", "Grounding text limit", Group = "rag", Min = 500, Unit = "chars",
        DependsOn = "ragEnabled")]
    public int MaxContextChars { get; set; } = 6000;

    /// <summary>Computed (not config): local RAG retrieval is enabled (<c>Rag:Enabled</c>).</summary>
    [LeafIgnore]
    public bool LocalEnabled { get; set; }

    /// <summary>Computed (not config): a web-search provider is configured (e.g. a Tavily API key is set).</summary>
    [LeafIgnore]
    public bool WebEnabled { get; set; }

    /// <summary>The <c>search</c> tool is offered iff at least one real source backs it .</summary>
    [LeafIgnore]
    public bool Available => LocalEnabled || WebEnabled;
}
