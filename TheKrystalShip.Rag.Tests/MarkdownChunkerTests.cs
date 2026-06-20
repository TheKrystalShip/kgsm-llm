using FluentAssertions;

using TheKrystalShip.Rag.Chunking;

namespace TheKrystalShip.Rag.Tests;

public class MarkdownChunkerTests
{
    private static MarkdownChunker Chunker(int size = 2000, int overlap = 200) =>
        new(new ChunkingOptions { ChunkSize = size, ChunkOverlap = overlap });

    [Fact]
    public void Nested_headings_become_the_breadcrumb()
    {
        const string md = "# Title\n\n## Section\n\nbody text here\n";

        var chunks = Chunker().Chunk(md, "doc.md");

        chunks.Should().ContainSingle();
        chunks[0].HeaderPath.Should().Be("Title > Section");
        chunks[0].Text.Should().StartWith("Title > Section");
        chunks[0].Text.Should().Contain("body text here");
        chunks[0].SourcePath.Should().Be("doc.md");
    }

    [Fact]
    public void Code_fence_is_kept_whole_and_a_hash_inside_it_is_not_a_heading()
    {
        const string md =
            "# Real Heading\n\nSome intro text.\n\n```bash\n# not a heading\necho hi\n```\n\nMore text.\n";

        var chunks = Chunker().Chunk(md, "doc.md");

        // The "# not a heading" line lives inside a fence, so it must not open a new section.
        chunks.Should().OnlyContain(c => c.HeaderPath == "Real Heading");
        chunks.Should().ContainSingle(c => c.Text.Contains("echo hi"));
        var withCode = chunks.Single(c => c.Text.Contains("echo hi"));
        withCode.Text.Should().Contain("# not a heading");
    }

    [Fact]
    public void Content_before_the_first_heading_has_an_empty_breadcrumb()
    {
        const string md = "intro paragraph with no heading\n\n# Later\n\nunder later\n";

        var chunks = Chunker().Chunk(md, "doc.md");

        chunks.Should().Contain(c => c.HeaderPath == string.Empty && c.Text.Contains("intro paragraph"));
        chunks.Should().Contain(c => c.HeaderPath == "Later" && c.Text.Contains("under later"));
    }

    [Fact]
    public void Oversized_prose_splits_into_overlapping_chunks()
    {
        // 200 chars of letters (no spaces/newlines) so trimming can't perturb the overlap math.
        var body = string.Concat(Enumerable.Range(0, 200).Select(i => (char)('a' + i % 26)));

        var chunks = Chunker(size: 50, overlap: 10).Chunk(body, "doc.md");

        chunks.Count.Should().BeGreaterThan(2);
        // Each chunk carries the previous chunk's trailing 10 chars as its head (the overlap).
        chunks[1].Text.Should().StartWith(chunks[0].Text[^10..]);
    }

    [Fact]
    public void Whitespace_only_input_yields_no_chunks()
    {
        Chunker().Chunk("   \n\n  \n", "doc.md").Should().BeEmpty();
    }
}
