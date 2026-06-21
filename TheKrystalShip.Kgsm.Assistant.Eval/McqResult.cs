using System.Text.Json;
using System.Text.Json.Serialization;

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

/// <summary>Per (item, condition) outcome, aggregated over reps. Keeps one sample reply for the transcript.</summary>
internal sealed record McqItemOutcome(
    string Id,
    string Topic,
    McqCondition Condition,
    char Expected,
    int Correct,
    int Parsed,
    int Reps,
    char? LastChosen,
    string? LastReply);

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
