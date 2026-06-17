using FluentAssertions;

namespace TheKrystalShip.Kgsm.Assistant.Cli.Tests;

/// <summary>
/// The streaming Markdown→ANSI renderer (§ rich line-mode). Two things are pinned down: the styled
/// subset (bold / inline code / headers / bullets / fenced blocks, NO italic), and — the property
/// that actually proves a streaming parser correct — that output is invariant to how the input is
/// chunked. When not styled it must be a byte-for-byte passthrough so a piped reply stays clean.
/// </summary>
public class MarkdownStreamRendererTests
{
    private static string Render(bool styled, params string[] deltas)
    {
        var sw = new StringWriter();
        var md = new MarkdownStreamRenderer(sw, styled);
        foreach (var d in deltas)
            md.Write(d);
        md.Complete();
        return sw.ToString();
    }

    [Fact]
    public void NotStyled_IsBytePassthrough()   // piped/redirected reply == raw model text
    {
        Render(styled: false, "**bold** `code`\n# h\n- x")
            .Should().Be("**bold** `code`\n# h\n- x");
    }

    [Fact]
    public void Bold_MarkersStripped_WrappedInSgr()
    {
        Render(true, "a **b** c").Should().Be("a \x1b[1mb\x1b[22m c");
    }

    [Fact]
    public void InlineCode_WrappedInColor()
    {
        Render(true, "use `go` now").Should().Be("use \x1b[36mgo\x1b[39m now");
    }

    [Fact]
    public void SingleStar_StaysLiteral_NoItalic()   // snake_case safety: * must not start italic
    {
        Render(true, "set *flag* on a_b_c").Should().Be("set *flag* on a_b_c");
    }

    [Fact]
    public void Header_RendersBold_HashAndSpaceConsumed()
    {
        Render(true, "# Title\n").Should().Be("\x1b[1mTitle\x1b[22m\n");
    }

    [Fact]
    public void NonHeader_HashWithoutSpace_StaysLiteral()
    {
        Render(true, "#tag\n").Should().Be("#tag\n");
    }

    [Fact]
    public void Bullet_MarkerBecomesGlyph()
    {
        Render(true, "- item\n").Should().Be("  \x1b[36m•\x1b[39m item\n");
    }

    [Fact]
    public void NonBullet_DashWithoutSpace_StaysLiteral()
    {
        Render(true, "-5 degrees\n").Should().Be("-5 degrees\n");
    }

    [Fact]
    public void BoldAtLineStart_IsBold_NotBullet()
    {
        Render(true, "**hi**\n").Should().Be("\x1b[1mhi\x1b[22m\n");
    }

    [Fact]
    public void Fence_WrapsContentDim_MarkersAndInfoStringConsumed()
    {
        // The closing fence and its trailing newline are consumed; the content keeps its own newline.
        Render(true, "```bash\nls -la\n```\n").Should().Be("\x1b[2mls -la\n\x1b[22m");
    }

    [Fact]
    public void Fence_SuppressesInlineParsing()   // ** / ` inside a code block are literal
    {
        Render(true, "```\na ** b `c`\n```\n").Should().Be("\x1b[2ma ** b `c`\n\x1b[22m");
    }

    [Fact]
    public void UnclosedBold_IsClosedAtNewline()   // an unmatched ** must not bleed past the line
    {
        Render(true, "**oops\nmore").Should().Be("\x1b[1moops\x1b[22m\nmore");
    }

    [Fact]
    public void UnclosedBold_IsClosedAtComplete()
    {
        Render(true, "**oops").Should().Be("\x1b[1moops\x1b[22m");
    }

    [Fact]
    public void LoneTrailingStar_FlushedLiteralAtComplete()
    {
        Render(true, "end *").Should().Be("end *");
    }

    [Theory]
    [InlineData(
        "# Heading\n- bullet one\n- bullet two\n\nRun **restart** with `kgsm restart`.\n" +
        "```\ncode block\nline two\n```\ndone *not bold* and `code`.\n")]
    [InlineData("plain text, no markdown at all, just a sentence about instance_name.")]
    [InlineData("**a****b** `c``d` mixing toggles and a trailing *")]
    [InlineData("`inline at start` then **bold** then\n## sub\n+ plus bullet\n")]
    public void OutputIsInvariantToChunking(string reference)
    {
        var whole = Render(true, reference);

        // Split at every single boundary — catches carry-buffer bugs (inside **, between # and the
        // space, mid-fence backticks, between - and the space) without hand-enumerating them.
        for (var k = 1; k < reference.Length; k++)
            Render(true, reference[..k], reference[k..])
                .Should().Be(whole, $"output must not depend on a split at index {k}");

        // And one character per delta — the worst case.
        var perChar = reference.Select(c => c.ToString()).ToArray();
        Render(true, perChar).Should().Be(whole, "output must not depend on one-char-per-delta chunking");
    }
}
