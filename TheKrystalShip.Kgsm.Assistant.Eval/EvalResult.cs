using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TheKrystalShip.Kgsm.Assistant.Eval;

/// <summary>One check's verdict in one rep.</summary>
internal sealed record CheckResultDto(string Label, string Dimension, bool Pass);

/// <summary>One turn's raw trajectory + per-check verdicts — kept so <c>compare</c> can show HOW a case
/// changed ("C8 went from staging-restart to asking-which"), not merely that a rate moved.</summary>
internal sealed record StepResultDto(
    int Step,
    string Prompt,
    IReadOnlyList<string> Tools,
    IReadOnlyList<string> Staged,
    int Iterations,
    string Outcome,
    string Final,
    IReadOnlyList<CheckResultDto> Checks);

internal sealed record RepResultDto(int Rep, IReadOnlyList<StepResultDto> Steps);

/// <summary>A check's pass-rate across all reps of a case, keyed stably (<c>s{step}:{label}</c>) for diffing.</summary>
internal sealed record CheckSummaryDto(string Key, string Label, string Dimension, int Passed, int Total, double Rate);

internal sealed record CaseResultDto(
    string Id,
    string Title,
    bool Authorized,
    bool Skipped,
    string? SkipReason,
    IReadOnlyList<RepResultDto> Reps,
    IReadOnlyList<CheckSummaryDto> Checks,
    IReadOnlyDictionary<string, double> Dimensions);

internal sealed record HostInfoDto(
    int Instances, int Running, int Stopped, int Blueprints, IReadOnlyDictionary<string, string?> Roles);

internal sealed record DimensionSummaryDto(string Dimension, int Passed, int Total, double Rate, bool Covered);

/// <summary>
/// How much of the run actually reached the model. Carried in the result because a score says nothing
/// about whether it was measured: an endpoint that dropped part-way produces errored turns whose
/// checks all fail, which is indistinguishable from a regression once the number is on its own.
/// Optional so result files written before it existed still load.
/// </summary>
internal sealed record RunHealthDto(int TurnsRun, int TurnsErrored, double ErrorRate, bool Degraded);

/// <summary>
/// A whole benchmark run, stamped with everything needed to compare it against another run honestly:
/// the model, sampling temp + seed regime, rep count, the corpus version, and the system-prompt
/// template hash (so a tuning edit is attributable). This is the durable artifact the user keeps.
/// </summary>
internal sealed record EvalRun(
    string Schema,
    string CorpusVersion,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string Model,
    double Temperature,
    int? Seed,
    int Reps,
    string? SystemPromptHash,
    HostInfoDto Host,
    IReadOnlyList<CaseResultDto> Cases,
    IReadOnlyList<DimensionSummaryDto> Summary,
    double OverallRate,
    RunHealthDto? Health = null)
{
    public const string CurrentSchema = "kgsm-assistant-eval/v1";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    public static EvalRun Load(string path) =>
        JsonSerializer.Deserialize<EvalRun>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException($"'{path}' is not a readable eval result.");
}
