using System.Text;

namespace TheKrystalShip.Rag.Chunking;

/// <summary>
/// Structure-aware markdown chunker. Splits a document along its heading hierarchy, keeps fenced
/// code blocks intact (never split mid-fence), prepends each chunk with its heading breadcrumb so
/// the embedding carries scope, and packs body text into <see cref="ChunkingOptions.ChunkSize"/>
/// chunks with <see cref="ChunkingOptions.ChunkOverlap"/> carry-over between adjacent chunks of
/// the same section. This is the retrieval-quality lever the benchmark author called "a fair bit
/// of tweaking."
/// </summary>
public sealed class MarkdownChunker
{
    private readonly ChunkingOptions _options;

    public MarkdownChunker(ChunkingOptions? options = null)
    {
        _options = options ?? new ChunkingOptions();
        if (_options.ChunkSize <= 0)
            throw new ArgumentException("ChunkSize must be positive.", nameof(options));
        if (_options.ChunkOverlap < 0 || _options.ChunkOverlap >= _options.ChunkSize)
            throw new ArgumentException("ChunkOverlap must be in [0, ChunkSize).", nameof(options));
    }

    /// <summary>Chunks one document. Returns an empty list for whitespace-only input.</summary>
    public IReadOnlyList<Chunk> Chunk(string text, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var chunks = new List<Chunk>();
        foreach (var (breadcrumb, body) in SplitSections(text))
        {
            foreach (var bodyChunk in PackBlocks(SplitBlocks(body)))
            {
                var content = breadcrumb.Length > 0 ? $"{breadcrumb}\n\n{bodyChunk}" : bodyChunk;
                chunks.Add(new Chunk(sourcePath, breadcrumb, content));
            }
        }
        return chunks;
    }

    /// <summary>Splits the doc into (heading-breadcrumb, body-lines) sections along its heading tree.</summary>
    private static IEnumerable<(string Breadcrumb, List<string> Body)> SplitSections(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var stack = new List<(int Level, string Title)>();
        var body = new List<string>();
        var sections = new List<(string, List<string>)>();
        var inFence = false;

        string Breadcrumb() => string.Join(" > ", stack.Select(s => s.Title));

        void Flush()
        {
            if (body.Count > 0)
            {
                sections.Add((Breadcrumb(), body));
                body = [];
            }
        }

        foreach (var line in lines)
        {
            if (line.AsSpan().TrimStart().StartsWith("```"))
            {
                inFence = !inFence;
                body.Add(line);
                continue;
            }

            if (!inFence && TryParseHeading(line, out var level, out var title))
            {
                Flush(); // body collected so far belongs to the previous breadcrumb
                while (stack.Count > 0 && stack[^1].Level >= level)
                    stack.RemoveAt(stack.Count - 1);
                stack.Add((level, title));
                continue; // the heading line itself is not body
            }

            body.Add(line);
        }

        Flush();
        return sections;
    }

    /// <summary>Splits a section body into blocks (blank-line-separated paragraphs; each code fence is one whole block).</summary>
    private static List<(string Text, bool IsCode)> SplitBlocks(List<string> bodyLines)
    {
        var blocks = new List<(string, bool)>();
        var current = new List<string>();
        var inFence = false;

        void Flush(bool isCode)
        {
            if (current.Count > 0)
            {
                var text = string.Join("\n", current).Trim('\n');
                if (text.Trim().Length > 0)
                    blocks.Add((text, isCode));
                current = [];
            }
        }

        foreach (var line in bodyLines)
        {
            if (line.AsSpan().TrimStart().StartsWith("```"))
            {
                if (!inFence)
                {
                    Flush(isCode: false); // close any open paragraph before the fence
                    inFence = true;
                    current.Add(line);
                }
                else
                {
                    current.Add(line);
                    inFence = false;
                    Flush(isCode: true); // the whole fence is one atomic block
                }
                continue;
            }

            if (inFence)
            {
                current.Add(line);
            }
            else if (line.Trim().Length == 0)
            {
                Flush(isCode: false); // blank line ends a paragraph
            }
            else
            {
                current.Add(line);
            }
        }

        Flush(inFence); // trailing content (or an unterminated fence)
        return blocks;
    }

    /// <summary>Greedily packs blocks into chunk bodies of ~ChunkSize, carrying overlap between adjacent chunks.</summary>
    private List<string> PackBlocks(List<(string Text, bool IsCode)> blocks)
    {
        var bodies = new List<string>();
        var sb = new StringBuilder();

        void Emit()
        {
            if (sb.Length == 0)
                return;
            var body = sb.ToString().Trim();
            bodies.Add(body);
            sb.Clear();
            var tail = OverlapTail(body, _options.ChunkOverlap);
            if (tail.Length > 0)
                sb.Append(tail);
        }

        foreach (var (text, isCode) in blocks)
        {
            foreach (var piece in SplitOversized(text, isCode, _options.ChunkSize))
            {
                if (sb.Length > 0 && sb.Length + piece.Length + 2 > _options.ChunkSize)
                    Emit();

                if (sb.Length > 0)
                    sb.Append("\n\n");
                sb.Append(piece);
            }
        }

        if (sb.Length > 0)
            bodies.Add(sb.ToString().Trim());

        return bodies;
    }

    /// <summary>Yields the block whole if it fits or is code; otherwise hard-splits an oversized prose block.</summary>
    private static IEnumerable<string> SplitOversized(string text, bool isCode, int maxSize)
    {
        if (isCode || text.Length <= maxSize)
        {
            yield return text;
            yield break;
        }

        for (var start = 0; start < text.Length; start += maxSize)
            yield return text.Substring(start, Math.Min(maxSize, text.Length - start));
    }

    /// <summary>Last <paramref name="overlap"/> chars of <paramref name="body"/>, trimmed to a clean line start; empty if the body is no larger than the overlap.</summary>
    private static string OverlapTail(string body, int overlap)
    {
        if (overlap <= 0 || body.Length <= overlap)
            return string.Empty;

        var tail = body[^overlap..];
        var newline = tail.IndexOf('\n');
        return newline >= 0 ? tail[(newline + 1)..] : tail;
    }

    private static bool TryParseHeading(string line, out int level, out string title)
    {
        level = 0;
        title = string.Empty;

        var span = line.AsSpan();
        var i = 0;
        while (i < span.Length && span[i] == ' ')
            i++;

        var hashStart = i;
        while (i < span.Length && span[i] == '#')
            i++;

        var hashes = i - hashStart;
        if (hashes is < 1 or > 6)
            return false;
        if (i >= span.Length || span[i] != ' ')
            return false;

        title = span[(i + 1)..].Trim().ToString();
        if (title.Length == 0)
            return false;

        level = hashes;
        return true;
    }
}
