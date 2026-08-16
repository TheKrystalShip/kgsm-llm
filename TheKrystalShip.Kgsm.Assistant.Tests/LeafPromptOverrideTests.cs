using FluentAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using TheKrystalShip.Llm.Models;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// The per-leaf layer of the override chain: a calling leaf's own file wins, the host-wide file
/// answers where the leaf has none, and a leaf that names nothing usable reads the assistant's own
/// text rather than nothing. Also pins that a leaf name cannot address a file outside the prompts
/// directory — it arrives over the wire and becomes a path segment.
/// </summary>
public sealed class LeafPromptOverrideTests : IDisposable
{
    private const string Leaf = "kgsm-bot";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kgsm-leafovr-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort */ }
    }

    private FilePromptOverrides Make()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [FilePromptOverrides.DirectoryKey] = _dir })
            .Build();
        return new FilePromptOverrides(config, NullLogger<FilePromptOverrides>.Instance);
    }

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void LeafFile_WinsOverTheHostWideFile()
    {
        Write("preamble.md", "HOST WIDE");
        Write(Path.Combine(Leaf, "preamble.md"), "THE BOT'S OWN");

        Make().ReadText("preamble.md", Leaf).Should().Be("THE BOT'S OWN");
    }

    /// <summary>
    /// The point of falling through rather than requiring a leaf to restate everything: a surface
    /// overrides the one segment that differs for it and inherits the rest.
    /// </summary>
    [Fact]
    public void SegmentTheLeafDoesNotOverride_FallsThroughToTheHostWideFile()
    {
        Write("preamble.md", "HOST WIDE");
        Write(Path.Combine(Leaf, "actions-allowed.md"), "THE BOT'S ACTIONS TEXT");

        var overrides = Make();
        overrides.ReadText("preamble.md", Leaf).Should().Be("HOST WIDE");
        overrides.ReadText("actions-allowed.md", Leaf).Should().Be("THE BOT'S ACTIONS TEXT");
    }

    [Fact]
    public void NoLeaf_ReadsTheHostWideFile()
    {
        Write("preamble.md", "HOST WIDE");
        Write(Path.Combine(Leaf, "preamble.md"), "THE BOT'S OWN");

        Make().ReadText("preamble.md").Should().Be("HOST WIDE");
    }

    /// <summary>An unknown leaf is not an error — it simply has no overrides of its own yet.</summary>
    [Fact]
    public void UnknownLeaf_ReadsTheHostWideFile_NeverNothing()
    {
        Write("preamble.md", "HOST WIDE");

        Make().ReadText("preamble.md", "kgsm-something-else").Should().Be("HOST WIDE");
    }

    /// <summary>
    /// A half-saved leaf override must not blank that surface's prompt, the same rule the host-wide
    /// layer already honours for itself.
    /// </summary>
    [Fact]
    public void BlankLeafFile_FallsThrough_RatherThanBlankingTheSurface()
    {
        Write("preamble.md", "HOST WIDE");
        Write(Path.Combine(Leaf, "preamble.md"), "   \n  ");

        Make().ReadText("preamble.md", Leaf).Should().Be("HOST WIDE");
    }

    /// <summary>
    /// The leaf name arrives over the wire and becomes a path segment. It is validated, not cleaned
    /// up, so no traversal can be assembled — and the caller lands on the host-wide text, not on
    /// whatever the path pointed at.
    /// </summary>
    [Theory]
    [InlineData("../secrets")]
    [InlineData("..")]
    [InlineData("kgsm/bot")]
    [InlineData("kgsm\\bot")]
    [InlineData("/etc")]
    [InlineData("KGSM-BOT")]
    [InlineData("-leading-hyphen")]
    [InlineData("")]
    public void AMalformedLeafName_IsRefused_AndReadsTheHostWideFile(string leaf)
    {
        Write("preamble.md", "HOST WIDE");

        Make().ReadText("preamble.md", leaf).Should().Be("HOST WIDE");
    }

    [Fact]
    public void ATraversingLeafName_CannotReachAFileOutsideThePromptsDirectory()
    {
        Write("preamble.md", "HOST WIDE");

        // Plant a file exactly where "../<sibling>" would land if the name were used verbatim.
        var sibling = _dir + "-sibling";
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "preamble.md"), "SHOULD NEVER BE READ");

        try
        {
            Make().ReadText("preamble.md", "../" + Path.GetFileName(sibling))
                .Should().Be("HOST WIDE");
        }
        finally
        {
            try { Directory.Delete(sibling, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ------------------------------------------------------------------ tools.json ---------------

/// <summary>
    /// Tool prose is where a surface's confirmation mechanic gets named, so a leaf's catalog follows
    /// the same rule its prompts do.
    /// </summary>
    /// <summary>
    /// Whole-file precedence, unlike the per-segment fall-through: a merged catalog would be half
    /// worded for a button and half for a card, which is worse than either alone.
    /// </summary>
}
