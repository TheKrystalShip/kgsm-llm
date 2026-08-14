using System.Text;

namespace TheKrystalShip.Kgsm.Assistant.Service.Speech;

/// <summary>
/// Cuts a reply arriving token by token into sentences worth speaking.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why cut it at all.</b> A whole answer synthesised when the turn ends arrives after the reader
/// has finished reading it. Sentence-sized pieces let the first one play while the model is still
/// writing the third, which is the entire point of doing this on the token stream.
/// </para>
/// <para>
/// ⚠ <b>Every character is examined exactly once.</b> Fence state carries across deltas, so anything
/// that re-reads text it has already seen toggles that state a second time and starts speaking the
/// code it was meant to skip. That is why text is consumed into the spoken buffer as it arrives
/// rather than being re-derived from a raw buffer whenever a sentence might be ready.
/// </para>
/// <para>
/// ⚠ <b>A fenced code block is not speech.</b> It spans sentences, its "sentences" are lines of
/// syntax, and reading one aloud is thirty seconds of punctuation. Everything between a pair of
/// fences is dropped, and an unclosed fence swallows the rest of the answer — which is correct: an
/// answer that opened a fence and stopped is a code block, whatever comes after it.
/// </para>
/// <para>
/// <b>What is dropped is markup, never words.</b> Nothing is summarised and nothing is cut short —
/// the whole reply is spoken, minus the characters that only mean something on a screen. A surface
/// that reworded a reply on its way to being read out would say things the assistant did not.
/// </para>
/// <para>
/// ⚠ This exists in two places for now — kgsm-bot cuts its own spoken replies the same way. They are
/// the same rules and want one implementation, in the speech package both already depend on; that
/// merge is owed and neither should drift in the meantime.
/// </para>
/// </remarks>
internal sealed class SpokenSentences
{
    /// <summary>
    /// The shortest run of speech worth its own request.
    /// </summary>
    /// <remarks>
    /// "Yes." on its own is a syllable of audio and a whole round trip to the engine, and it arrives
    /// as an abrupt little clip rather than as the start of an answer. Below this, the sentence waits
    /// for whatever follows it — and if nothing does, the flush at the end says it anyway.
    /// </remarks>
    private const int ShortestWorthSaying = 24;

    /// <summary>The line being read, raw. A line is the unit fence markers are recognised on.</summary>
    private readonly StringBuilder _line = new();

    /// <summary>Speech accumulated from lines already finished, waiting to be long enough to say.</summary>
    private readonly StringBuilder _said = new();

    private bool _fenced;

    /// <summary>
    /// Takes the next slice of the reply and returns whatever became sayable because of it.
    /// </summary>
    /// <remarks>
    /// A delta is a fragment of a token, so most calls return nothing. What ends a sentence is
    /// terminal punctuation or a line break — a heading and a list item are each their own line and
    /// each their own breath, which is how they should be read.
    /// </remarks>
    public IEnumerable<string> Take(string delta)
    {
        if (string.IsNullOrEmpty(delta)) yield break;

        foreach (char c in delta)
        {
            if (c == '\n')
            {
                EndLine();

                string? whole = Ready();
                if (whole is not null) yield return whole;
                continue;
            }

            // ⚠ A sentence ends at punctuation FOLLOWED BY SPACE, which is why the decision is taken
            // one character late. A dot with a letter after it is a version, a filename or a hostname —
            // `kgsm.sh`, `1.2.3`, `example.invalid` — and cutting there splits a sentence mid-word,
            // reads half of it aloud, and spends a synthesis request doing it.
            if (!_fenced && char.IsWhiteSpace(c) && EndsSentence())
            {
                EndLine();

                string? sentence = Ready();
                if (sentence is not null) yield return sentence;
                continue;
            }

            _line.Append(c);
        }
    }

    /// <summary>
    /// Whatever is left when the turn ends, said even if it never terminated.
    /// </summary>
    /// <remarks>
    /// A reply that ends without punctuation, or one shorter than the minimum, is still the answer —
    /// the minimum exists to avoid a clipped fragment mid-answer, not to swallow a short one.
    /// </remarks>
    public string? Flush()
    {
        EndLine();

        string left = _said.ToString().Trim();
        _said.Clear();

        return left.Length == 0 ? null : left;
    }

    /// <summary>Whether the line so far ends on terminal punctuation.</summary>
    private bool EndsSentence() =>
        _line.Length > 0 && _line[^1] is '.' or '!' or '?';

    /// <summary>
    /// Folds the line read so far into the spoken buffer, and clears it.
    /// </summary>
    /// <remarks>
    /// Called at a line break AND at terminal punctuation, so the same line can contribute twice —
    /// which is what makes "Two sentences. On one line." two of them. The fence toggle only ever
    /// happens here, once per line, because a marker is recognised by what a line starts with.
    /// </remarks>
    private void EndLine()
    {
        if (_line.Length == 0) return;

        string line = _line.ToString();
        _line.Clear();

        if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
        {
            _fenced = !_fenced;
            return;
        }

        if (_fenced) return;

        string bare = Bare(line);
        if (bare.Length == 0) return;

        if (_said.Length > 0) _said.Append(' ');
        _said.Append(bare);
    }

    /// <summary>The spoken buffer, when there is enough of it to be worth saying.</summary>
    private string? Ready()
    {
        if (_said.Length < ShortestWorthSaying) return null;

        string sentence = _said.ToString().Trim();
        _said.Clear();

        return sentence.Length == 0 ? null : sentence;
    }

    /// <summary>
    /// One line with its markup removed.
    /// </summary>
    /// <remarks>
    /// Deliberately small: the characters that would be read out as noise (a backtick becomes
    /// "backtick", a hash becomes "hash") and nothing else. A markdown parser here would be a second
    /// renderer with its own opinions about what a reply says.
    /// </remarks>
    private static string Bare(string line)
    {
        ReadOnlySpan<char> body = line.AsSpan().Trim();

        // A list marker or a heading hash is punctuation the ear does not need; the words after it are
        // the sentence. Only at the start of a line, so a hyphen inside a name survives.
        if (body.Length > 0 && body[0] is '-' or '*' or '#' or '>')
        {
            int at = 0;
            while (at < body.Length && (body[at] is '-' or '*' or '#' or '>' or ' ')) at++;
            body = body[at..];
        }

        var bare = new StringBuilder(body.Length);

        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];

            // A link's target is an address, not a sentence: "example.invalid/path" read aloud is
            // noise, and its dots would cut the sentence in the middle. The text stays, the URL goes.
            if (c == ']' && i + 1 < body.Length && body[i + 1] == '(')
            {
                int close = body[(i + 1)..].IndexOf(')');
                if (close >= 0)
                {
                    i += close + 1;
                    continue;
                }
            }

            // Emphasis, code spans and the pipes of a table: silent on a screen, spoken as characters
            // otherwise.
            if (c is '*' or '`' or '_' or '#' or '~' or '|' or '[' or ']') continue;

            bare.Append(c);
        }

        return bare.ToString().Trim();
    }
}
