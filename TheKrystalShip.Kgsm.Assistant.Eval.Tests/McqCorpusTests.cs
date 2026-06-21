using System.Runtime.CompilerServices;

using FluentAssertions;

namespace TheKrystalShip.Kgsm.Assistant.Eval.Tests;

/// <summary>
/// Guards the committed ground-truth corpus and its loader. The most dangerous failure mode is a
/// silently-malformed corpus (a bad answer key, a missing gold passage, a typo'd source filename) that
/// scores against a broken question, so the loader's validation is asserted both on the REAL shipped
/// corpus and on hand-built bad inputs.
/// </summary>
public class McqCorpusTests
{
    [Fact]
    public void The_shipped_corpus_loads_and_is_well_formed()
    {
        var corpus = McqCorpus.Load(ShippedQuestionsPath());

        corpus.Version.Should().NotBeNullOrWhiteSpace();
        corpus.Items.Should().HaveCountGreaterThanOrEqualTo(20, "the corpus must be sizeable enough to measure retrieval");
        corpus.Items.Select(i => i.Id).Should().OnlyHaveUniqueItems();
        // Load() already validated every answer key / gold / choice-count; reaching here proves it.
    }

    [Fact]
    public void Every_question_cites_a_source_file_that_exists_in_the_snapshot()
    {
        var corpus = McqCorpus.Load(ShippedQuestionsPath());
        var corpusDir = ShippedCorpusDir();

        foreach (var item in corpus.Items)
        {
            var path = Path.Combine(corpusDir, item.Source);
            File.Exists(path).Should().BeTrue($"MCQ {item.Id} cites source '{item.Source}' which must exist in the snapshot");
        }
    }

    [Fact]
    public void A_corpus_with_an_out_of_range_answer_is_rejected()
    {
        var bad = new McqCorpusFile("bad", new[]
        {
            new McqItem("X1", "t", "q?", new[] { "a", "b" }, "C", "src.md", "gold"),  // only A,B exist
        });

        var act = () => McqCorpus.Validate(bad, "test");
        act.Should().Throw<McqCorpusException>().WithMessage("*out of range*");
    }

    [Fact]
    public void A_corpus_with_a_duplicate_id_is_rejected()
    {
        var bad = new McqCorpusFile("bad", new[]
        {
            new McqItem("X1", "t", "q1?", new[] { "a", "b" }, "A", "src.md", "g1"),
            new McqItem("X1", "t", "q2?", new[] { "a", "b" }, "B", "src.md", "g2"),
        });

        var act = () => McqCorpus.Validate(bad, "test");
        act.Should().Throw<McqCorpusException>().WithMessage("*Duplicate*X1*");
    }

    [Fact]
    public void A_question_missing_its_gold_passage_is_rejected()
    {
        var bad = new McqCorpusFile("bad", new[]
        {
            new McqItem("X1", "t", "q?", new[] { "a", "b" }, "A", "src.md", "   "),  // blank gold
        });

        var act = () => McqCorpus.Validate(bad, "test");
        act.Should().Throw<McqCorpusException>().WithMessage("*gold*");
    }

    [Fact]
    public void A_question_with_fewer_than_two_choices_is_rejected()
    {
        var bad = new McqCorpusFile("bad", new[]
        {
            new McqItem("X1", "t", "q?", new[] { "only one" }, "A", "src.md", "gold"),
        });

        var act = () => McqCorpus.Validate(bad, "test");
        act.Should().Throw<McqCorpusException>().WithMessage("*two choices*");
    }

    [Fact]
    public void A_missing_file_is_a_clear_error()
    {
        var act = () => McqCorpus.Load(Path.Combine(Path.GetTempPath(), "definitely-not-here-" + Guid.NewGuid().ToString("N") + ".json"));
        act.Should().Throw<McqCorpusException>().WithMessage("*not found*");
    }

    // --- locate the REAL committed corpus from the test source location (not the copied output) -----

    private static string ShippedQuestionsPath([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..",
            "TheKrystalShip.Kgsm.Assistant.Eval", "mcq", "questions.json"));

    private static string ShippedCorpusDir([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..",
            "TheKrystalShip.Kgsm.Assistant.Eval", "mcq", "corpus"));
}
