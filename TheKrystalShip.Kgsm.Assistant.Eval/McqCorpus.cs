using System.Text.Json;

namespace TheKrystalShip.Kgsm.Assistant.Eval;

/// <summary>
/// Loads and validates the ground-truth MCQ corpus (<c>mcq/questions.json</c>). Validation is loud and
/// fail-fast: a malformed answer key or a missing gold passage would silently corrupt the lift chart,
/// so the loader throws <see cref="McqCorpusException"/> with a precise reason rather than scoring
/// against a broken question. Pure I/O + checks — no model, no Ollama.
/// </summary>
internal static class McqCorpus
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Default corpus path: shipped next to the binary (copied from <c>mcq/questions.json</c>).</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "mcq", "questions.json");

    /// <summary>Default corpus docs dir: the snapshot shipped next to the binary.</summary>
    public static string DefaultCorpusDir => Path.Combine(AppContext.BaseDirectory, "mcq", "corpus");

    public static McqCorpusFile Load(string path)
    {
        if (!File.Exists(path))
            throw new McqCorpusException($"MCQ corpus not found at '{path}'. Pass --mcq-file <path>.");

        McqCorpusFile? corpus;
        try
        {
            var json = File.ReadAllText(path);
            corpus = JsonSerializer.Deserialize<McqCorpusFile>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            throw new McqCorpusException($"Could not read the MCQ corpus '{path}': {ex.Message}", ex);
        }

        if (corpus is null || corpus.Items is null || corpus.Items.Count == 0)
            throw new McqCorpusException($"The MCQ corpus '{path}' is empty.");
        if (string.IsNullOrWhiteSpace(corpus.Version))
            throw new McqCorpusException($"The MCQ corpus '{path}' is missing a version stamp.");

        Validate(corpus, path);
        return corpus;
    }

    /// <summary>The same checks the loader runs — exposed so a test can assert a hand-built corpus is well-formed.</summary>
    public static void Validate(McqCorpusFile corpus, string source)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in corpus.Items)
        {
            string Where(string what) => $"MCQ '{item.Id}' in '{source}': {what}";

            if (string.IsNullOrWhiteSpace(item.Id))
                throw new McqCorpusException($"An MCQ in '{source}' has a blank id.");
            if (!seen.Add(item.Id))
                throw new McqCorpusException($"Duplicate MCQ id '{item.Id}' in '{source}'.");
            if (string.IsNullOrWhiteSpace(item.Question))
                throw new McqCorpusException(Where("blank question."));
            if (item.Choices is null || item.Choices.Count < 2)
                throw new McqCorpusException(Where("needs at least two choices."));
            if (item.Choices.Count > 26)
                throw new McqCorpusException(Where("has more than 26 choices."));
            if (item.Choices.Any(string.IsNullOrWhiteSpace))
                throw new McqCorpusException(Where("has a blank choice."));
            if (string.IsNullOrWhiteSpace(item.Answer) || item.Answer.Trim().Length != 1)
                throw new McqCorpusException(Where($"answer must be a single letter, got '{item.Answer}'."));

            var letter = item.AnswerLetter;
            var maxLetter = (char)('A' + item.Choices.Count - 1);
            if (letter < 'A' || letter > maxLetter)
                throw new McqCorpusException(Where($"answer '{letter}' is out of range (choices A..{maxLetter})."));
            if (string.IsNullOrWhiteSpace(item.Gold))
                throw new McqCorpusException(Where("missing the gold passage (needed for the oracle condition)."));
            if (string.IsNullOrWhiteSpace(item.Source))
                throw new McqCorpusException(Where("missing the source file."));
        }
    }
}

/// <summary>Raised when the MCQ corpus is missing, unreadable, or malformed.</summary>
internal sealed class McqCorpusException : Exception
{
    public McqCorpusException(string message) : base(message) { }
    public McqCorpusException(string message, Exception inner) : base(message, inner) { }
}
