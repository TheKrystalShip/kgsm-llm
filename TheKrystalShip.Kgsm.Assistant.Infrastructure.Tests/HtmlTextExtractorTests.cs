using FluentAssertions;

using TheKrystalShip.Kgsm.Assistant.Infrastructure.Fetch;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Tests;

/// <summary>Unit-tests the dependency-free HTML → text extractor in isolation from the HTTP layer.</summary>
public class HtmlTextExtractorTests
{
    [Fact]
    public void ExtractText_StripsTagsScriptsAndStyles()
    {
        const string html = """
            <html><head><title>Setup Guide</title>
            <style>body { color: red; }</style>
            <script>alert('hi');</script>
            </head>
            <body>
              <h1>Setup Guide</h1>
              <p>Download the <b>server</b> binary and run it.</p>
              <script>trackPageView();</script>
            </body></html>
            """;

        var text = HtmlTextExtractor.ExtractText(html);

        text.Should().Contain("Setup Guide");
        text.Should().Contain("Download the");
        text.Should().Contain("server");
        text.Should().Contain("binary and run it.");
        text.Should().NotContain("color: red");
        text.Should().NotContain("alert(");
        text.Should().NotContain("trackPageView");
        text.Should().NotContain("<");
        text.Should().NotContain(">");
    }

    [Fact]
    public void ExtractText_DecodesHtmlEntities()
    {
        var text = HtmlTextExtractor.ExtractText("<p>Ports &amp; firewalls &mdash; &quot;quoted&quot; &lt;value&gt;</p>");

        text.Should().Contain("Ports & firewalls");
        text.Should().Contain("\"quoted\"");
        text.Should().Contain("<value>");
    }

    [Fact]
    public void ExtractText_CollapsesRunsOfWhitespace()
    {
        var text = HtmlTextExtractor.ExtractText("<p>a</p>\n\n\n\n<p>b</p>");

        text.Should().NotContain("\n\n\n");
    }

    [Fact]
    public void ExtractTitle_ReturnsDecodedTrimmedTitle()
    {
        var title = HtmlTextExtractor.ExtractTitle("<html><head><title>  KGSM &mdash; Docs  </title></head><body></body></html>");

        title.Should().Be("KGSM — Docs");
    }

    [Fact]
    public void ExtractTitle_ReturnsNull_WhenNoTitleTag()
    {
        HtmlTextExtractor.ExtractTitle("<html><body><p>no title here</p></body></html>").Should().BeNull();
    }

    [Fact]
    public void ExtractTitle_ReturnsNull_WhenBlank()
    {
        HtmlTextExtractor.ExtractTitle("<html><head><title>   </title></head></html>").Should().BeNull();
    }
}
