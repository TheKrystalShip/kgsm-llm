using System.Text.Json;
using System.Text.Json.Serialization;

using TheKrystalShip.Rag.Index;

namespace TheKrystalShip.Kgsm.Assistant.Eval;

/// <summary>The three measured conditions whose accuracies form the lift chart (plan §7, Phase 5).</summary>
internal enum McqCondition
{
    /// <summary>No context — the model answers from parametric knowledge alone (the baseline).</summary>
    ClosedBook,

    /// <summary>The model is handed whatever the real <see cref="SearchAggregator"/> retrieves from the index.</summary>
    WithRag,

    /// <summary>The model is handed the gold passage directly — the retrieval ceiling.</summary>
    Oracle,
}

internal static class McqConditions
{
    public static string Label(this McqCondition c) => c switch
    {
        McqCondition.ClosedBook => "closed-book",
        McqCondition.WithRag => "with-rag",
        McqCondition.Oracle => "oracle",
        _ => c.ToString(),
    };

    public static bool TryParse(string token, out McqCondition condition)
    {
        switch (token.Trim().ToLowerInvariant())
        {
            case "closed" or "closed-book" or "closedbook" or "none" or "no-rag":
                condition = McqCondition.ClosedBook; return true;
            case "rag" or "with-rag" or "withrag" or "local":
                condition = McqCondition.WithRag; return true;
            case "oracle" or "gold":
                condition = McqCondition.Oracle; return true;
            default:
                condition = default; return false;
        }
    }
}

/// <summary>
/// The retrieval/chunking knobs Phase 5 tunes — the SAME ones production uses (chunk size/overlap at
/// build time; TopK + MinScore + LocalMinScore + MaxContextChars at query time), so a value that wins
/// here carries to what the <see cref="SearchAggregator"/> ships.
/// </summary>
internal sealed record RagTuning(
    int ChunkSize,
    int ChunkOverlap,
    int TopK,
    double MinScore,
    double LocalMinScore,
    int MaxContextChars)
{
    /// <summary>Defaults mirror the shipped <c>Rag</c> config + <see cref="SearchOptions"/> starting guesses.</summary>
    public static RagTuning Default => new(
        ChunkSize: 2000, ChunkOverlap: 200, TopK: 5, MinScore: 0.0, LocalMinScore: 0.35, MaxContextChars: 6000);
}

/// <summary>Per (item, condition) outcome, aggregated over reps. Keeps one sample reply for the transcript,
/// and (with-RAG only) the retrieval diagnostic that drives the Phase 6 lever decision.</summary>
internal sealed record McqItemOutcome(
    string Id,
    string Topic,
    McqCondition Condition,
    char Expected,
    int Correct,
    int Parsed,
    int Reps,
    char? LastChosen,
    string? LastReply,
    RetrievalDiagnostic? Retrieval = null);

/// <summary>
/// One raw cosine top-k hit, annotated for the retrieval read (Phase 6): where it came from, its score,
/// how much of the gold passage it lexically covers (<see cref="TextOverlap"/>), whether it survived the
/// <c>MaxContextChars</c> cap into the model's context, and whether it's from the gold's own source doc.
/// </summary>
internal sealed record RetrievedHit(
    string SourcePath,
    string HeaderPath,
    double Score,
    double GoldOverlap,
    bool Survived,
    bool RightDoc);

/// <summary>
/// The with-RAG retrieval outcome for one question: the raw <see cref="VectorSearch"/> top-k (ranked),
/// captured BEFORE any MinScore filter so recall@k is honest, plus the survived-into-context flags. This
/// is a measurement of retrieval, never of the answer — it tells us WHERE the with-rag → oracle gap lives
/// (the gold was never retrieved vs. retrieved-but-dropped vs. in-context-but-the-model-was-wrong).
/// </summary>
internal sealed record RetrievalDiagnostic(IReadOnlyList<RetrievedHit> Hits)
{
    /// <summary>Best gold coverage across the raw top-k — the quantity recall@k thresholds.</summary>
    public double BestGoldOverlapTopK => Hits.Count == 0 ? 0 : Hits.Max(h => h.GoldOverlap);

    /// <summary>Best gold coverage among only the chunks that survived the context-char cap.</summary>
    public double BestGoldOverlapContext =>
        Hits.Where(h => h.Survived).Select(h => h.GoldOverlap).DefaultIfEmpty(0).Max();

    /// <summary>Did any retrieved chunk come from the gold's own source document?</summary>
    public bool RightDocInTopK => Hits.Any(h => h.RightDoc);

    /// <summary>How many of the raw top-k chunks made it past the <c>MaxContextChars</c> cap.</summary>
    public int SurvivedCount => Hits.Count(h => h.Survived);

    /// <summary>Top cosine score (rank-1) — the value the aggregator's LocalMinScore gate sees.</summary>
    public double TopScore => Hits.Count == 0 ? 0 : Hits.Max(h => h.Score);
}

/// <summary>
/// The three retrieval failure modes the Phase 6 gate distinguishes (advisor's a/b/c). The lever each
/// implies is different, which is the whole point of classifying before building: only (a) is a job for
/// a hybrid retriever; (c) is not a retrieval problem at all.
/// </summary>
internal enum RetrievalBucket
{
    /// <summary>(a) The gold passage never made the raw top-k → a recall failure (BM25 hybrid / larger k).</summary>
    GoldMissedTopK,

    /// <summary>(b) Gold was in the top-k but didn't survive the context cap / score floor (top-k / context / re-rank).</summary>
    GoldDroppedFromContext,

    /// <summary>(c) Gold WAS in the model's context, yet it answered wrong → a model ceiling; no retrieval lever helps.</summary>
    GoldInContext,
}

/// <summary>Buckets a with-RAG retrieval into one of the three Phase 6 failure modes by gold coverage.</summary>
internal static class RetrievalDiagnosis
{
    /// <summary>Coverage at/above which a chunk is judged to actually contain the gold passage. Gold
    /// excerpts are short and chunks are ~2000 chars, so a real hit lands near 1.0; 0.5 is a safe cut.</summary>
    public const double GoldThreshold = 0.5;

    public static RetrievalBucket Classify(RetrievalDiagnostic diagnostic, double threshold = GoldThreshold)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        if (diagnostic.BestGoldOverlapTopK < threshold)
            return RetrievalBucket.GoldMissedTopK;
        if (diagnostic.BestGoldOverlapContext < threshold)
            return RetrievalBucket.GoldDroppedFromContext;
        return RetrievalBucket.GoldInContext;
    }
}

/// <summary>Accuracy roll-up for one condition across the whole corpus.</summary>
internal sealed record McqConditionSummary(McqCondition Condition, int Correct, int Parsed, int Total)
{
    public double Accuracy => Total == 0 ? 0 : (double)Correct / Total;
    public double ParseRate => Total == 0 ? 0 : (double)Parsed / Total;
    public int Unparsed => Total - Parsed;
}

/// <summary>One value of a swept knob and the with-RAG accuracy it produced.</summary>
internal sealed record McqSweepPoint(string Value, int Correct, int Parsed, int Total)
{
    public double Accuracy => Total == 0 ? 0 : (double)Correct / Total;
}

/// <summary>A with-RAG sweep over a single knob (the rest held at baseline).</summary>
internal sealed record McqSweepAxis(string Knob, IReadOnlyList<McqSweepPoint> Points);

/// <summary>The stamped result of one MCQ eval run — written to disk for the record (mirrors <c>EvalRun</c>).</summary>
internal sealed record McqRun(
    string Schema,
    string CorpusVersion,
    string Model,
    double Temperature,
    int? Seed,
    int Reps,
    RagTuning Tuning,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    IReadOnlyList<McqConditionSummary> Conditions,
    IReadOnlyList<McqItemOutcome> Items,
    IReadOnlyList<McqSweepAxis> Sweeps)
{
    public const string CurrentSchema = "mcq-1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}
