namespace TheKrystalShip.Llm.Models;

/// <summary>
/// How one tool behaved across the corpus — the "which tools does the model reach for, and how do
/// they behave when it does" read. <see cref="FailedCalls"/> counts calls whose model-facing output
/// followed the <c>"Error: …"</c> convention every dispatcher writes on failure
/// (<see cref="ToolOutput"/>); it is a property of the recorded output, not a guess.
/// <para>
/// The name is reported exactly as the log holds it, including a name no catalog defines — a model
/// that invents a tool records a call like any other, and hiding it would erase the most useful
/// signal in the set. Deciding whether a name IS in the catalog belongs to the surface that owns the
/// catalog, never to this library.
/// </para>
/// </summary>
public sealed record ToolStat
{
    /// <summary>The tool name as recorded on the turn.</summary>
    public required string Name { get; init; }

    /// <summary>How many times it was called.</summary>
    public required int Calls { get; init; }

    /// <summary>Median wall-clock duration, in milliseconds.</summary>
    public required long MedianMs { get; init; }

    /// <summary>Slowest recorded call, in milliseconds.</summary>
    public required long MaxMs { get; init; }

    /// <summary>Calls whose output was an <c>"Error: …"</c> string.</summary>
    public required int FailedCalls { get; init; }
}

/// <summary>
/// One system-prompt version's slice of the corpus, bucketed by
/// <see cref="ConversationTurnRecord.SystemPromptHash"/>. This is what the hash is recorded for:
/// change the prompt, and the next bucket is directly comparable to the last.
/// </summary>
public sealed record PromptVersionStat
{
    /// <summary>The short system-prompt hash; <c>null</c> for turns recorded without one.</summary>
    public string? Hash { get; init; }

    /// <summary>Turns driven by this prompt version.</summary>
    public required int Turns { get; init; }

    /// <summary>Of <see cref="Turns"/>, how many ended <see cref="TurnOutcome.Ok"/>.</summary>
    public required int OkTurns { get; init; }

    /// <summary>Median answer time for this version, in milliseconds; <c>null</c> when no turn of it was timed.</summary>
    public long? MedianMs { get; init; }
}

/// <summary>Turns recorded on one calendar day (UTC), for the activity strip.</summary>
public sealed record DailyTurnCount
{
    /// <summary>The day, as <c>yyyy-MM-dd</c> in UTC.</summary>
    public required string Date { get; init; }

    /// <summary>Turns started that day.</summary>
    public required int Turns { get; init; }
}

/// <summary>
/// The whole-corpus roll-up behind an operator's "how is this assistant doing" view — derived from
/// the append-only turn log, never from a counter kept alongside it, so it can never disagree with
/// the transcripts it summarizes.
/// <para>
/// Every distribution figure is <b>nullable and null when nothing was measured</b>: a corpus with no
/// timed turn reports <c>null</c> for the durations rather than a zero that reads like a
/// measurement. A count is a count and is zero when the thing genuinely did not happen — the two are
/// deliberately different kinds of answer.
/// </para>
/// </summary>
public sealed record ConversationStats
{
    /// <summary>Conversations in the surface, <b>including</b> soft-deleted ones.</summary>
    public required int Conversations { get; init; }

    /// <summary>Of <see cref="Conversations"/>, how many are soft-deleted.</summary>
    public required int DeletedConversations { get; init; }

    /// <summary>Distinct <c>surface:user</c> actors.</summary>
    public required int Actors { get; init; }

    /// <summary>Completed turns (checkpoints excluded).</summary>
    public required int Turns { get; init; }

    /// <summary>Turns that reached a final reply.</summary>
    public required int OkTurns { get; init; }

    /// <summary>Turns that failed in the backend.</summary>
    public required int ErrorTurns { get; init; }

    /// <summary>Turns that exhausted the iteration cap without a final answer.</summary>
    public required int CapHitTurns { get; init; }

    /// <summary>Turns abandoned mid-flight.</summary>
    public required int CancelledTurns { get; init; }

    /// <summary>
    /// Turns whose payload carries no outcome at all — recorded before the field existed. They are
    /// counted apart from the four outcomes rather than folded into one, because assuming they
    /// succeeded would overstate the success rate.
    /// </summary>
    public required int UnrecordedOutcomeTurns { get; init; }

    /// <summary>Median answer time in ms; <c>null</c> when no turn carried usable timestamps.</summary>
    public long? MedianTurnMs { get; init; }

    /// <summary>95th-percentile answer time in ms (nearest-rank); <c>null</c> when nothing was timed.</summary>
    public long? P95TurnMs { get; init; }

    /// <summary>Slowest answer in ms; <c>null</c> when nothing was timed.</summary>
    public long? MaxTurnMs { get; init; }

    /// <summary>Median model↔tool round-trips per turn; <c>null</c> for an empty corpus.</summary>
    public int? MedianIterations { get; init; }

    /// <summary>Most round-trips any single turn ran; <c>null</c> for an empty corpus.</summary>
    public int? MaxIterations { get; init; }

    /// <summary>Median share of the context window occupied, as a percentage; <c>null</c> when no turn reported usage.</summary>
    public double? MedianContextPercent { get; init; }

    /// <summary>Highest share of the context window ever occupied, as a percentage; <c>null</c> when none reported.</summary>
    public double? MaxContextPercent { get; init; }

    /// <summary>
    /// The context window the reported turns ran against, when every one of them agrees;
    /// <c>null</c> when the corpus spans more than one window (the percentages then describe
    /// different denominators and a single number would misrepresent them).
    /// </summary>
    public int? ContextWindow { get; init; }

    /// <summary>Turns that ran with thinking mode on.</summary>
    public required int ThinkingTurns { get; init; }

    /// <summary>Turns that called no tool at all (pure prose).</summary>
    public required int TurnsWithoutTool { get; init; }

    /// <summary>Total tool invocations across every turn.</summary>
    public required int ToolCalls { get; init; }

    /// <summary>Per-tool behaviour, most-called first.</summary>
    public required IReadOnlyList<ToolStat> Tools { get; init; }

    /// <summary>Per-system-prompt-version slices, largest first.</summary>
    public required IReadOnlyList<PromptVersionStat> PromptVersions { get; init; }

    /// <summary>Turns per day (UTC), oldest first.</summary>
    public required IReadOnlyList<DailyTurnCount> Activity { get; init; }
}
