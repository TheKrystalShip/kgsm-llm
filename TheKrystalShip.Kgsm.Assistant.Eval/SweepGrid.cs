namespace TheKrystalShip.Kgsm.Assistant.Eval;

/// <summary>
/// Resolves a <c>--sweep &lt;knob&gt;</c> name into the canonical knob, the grid of values to try, and a
/// function that applies one value to the baseline <see cref="RagTuning"/> (holding every other knob
/// fixed). One axis at a time keeps the run cheap and the cause of an accuracy move unambiguous — a
/// full grid is a manual series of single-axis sweeps. Pure; unit-testable without a model.
/// </summary>
internal static class SweepGrid
{
    public static IReadOnlyList<string> KnownKnobs { get; } =
        new[] { "chunk-size", "chunk-overlap", "top-k", "min-score", "local-min-score", "max-context" };

    public static (string Canonical, IReadOnlyList<string> Values, Func<string, RagTuning> Apply) Resolve(
        string knob, RagTuning baseline)
    {
        switch (Canonicalize(knob))
        {
            case "chunk-size":
                return ("chunk-size", Strings(800, 1200, 2000, 3000),
                    v => baseline with { ChunkSize = int.Parse(v) });
            case "chunk-overlap":
                return ("chunk-overlap", Strings(0, 100, 200, 400),
                    v => baseline with { ChunkOverlap = int.Parse(v) });
            case "top-k":
                return ("top-k", Strings(1, 3, 5, 8),
                    v => baseline with { TopK = int.Parse(v) });
            case "min-score":
                return ("min-score", Strings(0.0, 0.2, 0.35, 0.5),
                    v => baseline with { MinScore = double.Parse(v, System.Globalization.CultureInfo.InvariantCulture) });
            case "local-min-score":
                return ("local-min-score", Strings(0.2, 0.35, 0.5, 0.65),
                    v => baseline with { LocalMinScore = double.Parse(v, System.Globalization.CultureInfo.InvariantCulture) });
            case "max-context":
                return ("max-context", Strings(2000, 4000, 6000, 10000),
                    v => baseline with { MaxContextChars = int.Parse(v) });
            default:
                throw new McqRunException(
                    $"unknown --sweep knob '{knob}'. Known knobs: {string.Join(", ", KnownKnobs)}.");
        }
    }

    /// <summary>Normalizes the various spellings a user might type (e.g. <c>topk</c>, <c>k</c>, <c>chunk</c>).</summary>
    public static string Canonicalize(string knob) => knob.Trim().ToLowerInvariant() switch
    {
        "chunk-size" or "chunk" or "chunksize" or "size" => "chunk-size",
        "chunk-overlap" or "overlap" or "chunkoverlap" => "chunk-overlap",
        "top-k" or "topk" or "k" => "top-k",
        "min-score" or "minscore" or "min" => "min-score",
        "local-min-score" or "localminscore" or "local" or "lms" => "local-min-score",
        "max-context" or "maxcontext" or "context" or "max" => "max-context",
        _ => knob.Trim().ToLowerInvariant(),
    };

    private static string[] Strings(params int[] values) =>
        values.Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();

    private static string[] Strings(params double[] values) =>
        values.Select(v => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).ToArray();
}
