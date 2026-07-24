using System.Net;
using System.Text.RegularExpressions;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Fetch;

/// <summary>
/// A lightweight, dependency-free HTML → text extractor for <c>fetch_url</c>. Deliberately NOT a
/// real HTML parser (no reflection-based library like AngleSharp/HtmlAgilityPack — would bloat the
/// assistant for a job regex handles well enough): strips <c>&lt;script&gt;</c>/<c>&lt;style&gt;</c>
/// blocks, turns block-level closing tags into line breaks so paragraphs don't run together, strips
/// the remaining tags, decodes HTML entities via the BCL's <see cref="WebUtility"/> (no library dep),
/// and collapses whitespace. Good enough for LLM-readable grounding text; not a layout-faithful render.
/// </summary>
internal static class HtmlTextExtractor
{
    private static readonly Regex ScriptStyle = new(
        @"<(script|style|noscript)\b[^>]*>.*?</\1\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BlockBreaks = new(
        @"</(p|div|br|li|tr|h[1-6])\s*>|<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Tags = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"[ \t]+", RegexOptions.Compiled);
    private static readonly Regex BlankLines = new(@"\n{3,}", RegexOptions.Compiled);
    private static readonly Regex TitleTag = new(
        @"<title[^>]*>(.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The page's <c>&lt;title&gt;</c> text, decoded and trimmed — null if absent/blank.
    /// Cheap: one regex over the raw HTML, no full parse.</summary>
    public static string? ExtractTitle(string html)
    {
        var match = TitleTag.Match(html);
        if (!match.Success)
            return null;

        var title = WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
        return title.Length == 0 ? null : title;
    }

    /// <summary>Strips markup down to readable text, preserving rough paragraph structure.</summary>
    public static string ExtractText(string html)
    {
        var noScripts = ScriptStyle.Replace(html, " ");
        // Mark each block boundary as a paragraph break (\n\n); adjacent boundaries naturally collapse
        // to the same gap below, so this doesn't produce runaway blank lines.
        var withBreaks = BlockBreaks.Replace(noScripts, "\n\n");
        var stripped = Tags.Replace(withBreaks, " ");
        var decoded = WebUtility.HtmlDecode(stripped);
        var collapsedSpaces = Spaces.Replace(decoded, " ");
        var trimmedLines = string.Join('\n', collapsedSpaces.Split('\n').Select(l => l.Trim()));
        var collapsedBlank = BlankLines.Replace(trimmedLines, "\n\n");
        return collapsedBlank.Trim();
    }
}
