namespace TheKrystalShip.Kgsm.Assistant.Eval;

/// <summary>
/// One ground-truth multiple-choice question, drawn from the real-docs corpus. Unlike the routing
/// <see cref="BenchmarkCase"/> (which scores what tool the model CALLED), an <see cref="McqItem"/>
/// scores whether the model's ANSWER is correct — the only thing that reproduces the no-RAG / with-RAG
/// / oracle lift chart (plan §7, Phase 5). Each item ships its own <see cref="Gold"/> passage so the
/// oracle condition is independent of retrieval and stable across doc edits.
/// </summary>
/// <param name="Id">Stable identifier (e.g. <c>Q1</c>), used in the per-item breakdown and result files.</param>
/// <param name="Topic">Coarse subject tag (e.g. <c>architecture</c>) for the per-topic roll-up.</param>
/// <param name="Question">The question stem — deliberately phrased away from the source wording (retrieval-stressing).</param>
/// <param name="Choices">The answer options in order; index 0 = choice <c>A</c>, 1 = <c>B</c>, …</param>
/// <param name="Answer">The correct choice letter (<c>A</c>..), validated against <see cref="Choices"/> on load.</param>
/// <param name="Source">The corpus file the answer is grounded in — provenance, not used for scoring.</param>
/// <param name="Gold">The gold passage handed to the model in the oracle condition (the retrieval ceiling).</param>
internal sealed record McqItem(
    string Id,
    string Topic,
    string Question,
    IReadOnlyList<string> Choices,
    string Answer,
    string Source,
    string Gold)
{
    /// <summary>The correct choice as an uppercase letter (whitespace-trimmed).</summary>
    public char AnswerLetter => char.ToUpperInvariant(Answer.Trim()[0]);

    /// <summary>Renders the choices as <c>A. …</c> lines for the prompt.</summary>
    public string FormatChoices()
    {
        var lines = new string[Choices.Count];
        for (var i = 0; i < Choices.Count; i++)
            lines[i] = $"{(char)('A' + i)}. {Choices[i]}";
        return string.Join("\n", lines);
    }
}

/// <summary>The on-disk corpus envelope: a stamped version (bump when questions change, mirroring
/// <see cref="BenchmarkSuite.Version"/>) plus the items.</summary>
internal sealed record McqCorpusFile(string Version, IReadOnlyList<McqItem> Items);
