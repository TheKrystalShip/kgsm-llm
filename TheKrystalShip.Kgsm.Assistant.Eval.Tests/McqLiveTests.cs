using System.Runtime.CompilerServices;

using FluentAssertions;

namespace TheKrystalShip.Kgsm.Assistant.Eval.Tests;

/// <summary>
/// End-to-end smoke of the MCQ runner against a REAL Ollama (chat + embedder): builds an index from
/// the shipped snapshot and answers a small subset under all three conditions. Gated on
/// <c>KGSM_LIVE_OLLAMA=1</c> (mirrors the other live smokes). Asserts the pipeline produces well-formed
/// summaries and that the oracle condition — handed the gold passage — answers correctly; it does NOT
/// assert the closed→rag→oracle ordering (that's the real run's job, not a 2-item smoke).
/// </summary>
public sealed class McqLiveTests
{
    [Fact]
    public async Task Runs_all_three_conditions_over_a_small_subset()
    {
        if (Environment.GetEnvironmentVariable("KGSM_LIVE_OLLAMA") != "1")
            return;

        var full = McqCorpus.Load(ShippedQuestionsPath());
        var subset = new McqCorpusFile(full.Version, full.Items.Take(2).ToList());

        var config = new McqRunConfig(
            Model: "gemma4:12b",
            Endpoint: null,
            Temperature: 0.0,
            Seed: null,
            Reps: 1,
            EmbeddingModel: null,
            CorpusDir: ShippedCorpusDir(),
            McqFile: ShippedQuestionsPath(),
            Conditions: new[] { McqCondition.ClosedBook, McqCondition.WithRag, McqCondition.Oracle },
            Tuning: RagTuning.Default,
            SweepKnob: null,
            Verbose: false);

        using var runner = McqRunner.Build(config);
        var run = await runner.RunAsync(subset, CancellationToken.None);

        run.Conditions.Should().HaveCount(3);
        run.Conditions.Should().OnlyContain(c => c.Total == 2);

        var oracle = run.Conditions.Single(c => c.Condition == McqCondition.Oracle);
        oracle.Correct.Should().BeGreaterThan(0, "handing the model the gold passage should let it answer");
    }

    private static string ShippedQuestionsPath([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..",
            "TheKrystalShip.Kgsm.Assistant.Eval", "mcq", "questions.json"));

    private static string ShippedCorpusDir([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..",
            "TheKrystalShip.Kgsm.Assistant.Eval", "mcq", "corpus"));
}
