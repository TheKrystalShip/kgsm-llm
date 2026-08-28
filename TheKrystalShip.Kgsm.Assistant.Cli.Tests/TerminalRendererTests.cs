using FluentAssertions;

using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Cli.Tests;

/// <summary>
/// The event→terminal mapping (§4), driven directly with scripted <see cref="AssistantStreamEvent"/>s
/// and captured <see cref="StringWriter"/>s. (AssistantStreamEvent / PendingConfirmation /
/// ConfirmationKind resolve via the enclosing TheKrystalShip.Kgsm.Assistant namespace.)
/// </summary>
public class TerminalRendererTests
{
    private static (TerminalRenderer renderer, StringWriter @out, StringWriter err) Make(
        bool showStatus, bool color, bool showThinking = true)
    {
        var @out = new StringWriter();
        var err = new StringWriter();
        return (new TerminalRenderer(@out, err, showStatus, color, showThinking), @out, err);
    }

    [Fact]
    public void Tokens_StreamToStdout_NoNewlineUntilFinal()
    {
        var (renderer, @out, err) = Make(showStatus: true, color: false);

        renderer.Handle(AssistantStreamEvent.Token("Hello, "));
        renderer.Handle(AssistantStreamEvent.Token("world"));
        @out.ToString().Should().Be("Hello, world");   // no newline yet

        renderer.Handle(AssistantStreamEvent.Final("Hello, world"));
        @out.ToString().Should().Be("Hello, world" + Environment.NewLine);
        err.ToString().Should().BeEmpty();   // reply text never leaks to stderr
    }

    [Fact]
    public void ToolStart_WritesDimStatusToStderr_WhenStatusShown()
    {
        var (renderer, @out, err) = Make(showStatus: true, color: false);

        renderer.Handle(AssistantStreamEvent.ToolStart(
            new Tool("get_status"), new Dictionary<string, string?> { ["instance"] = "terraria" }));

        err.ToString().Should().Contain("→ get_status(instance=terraria)");
        @out.ToString().Should().BeEmpty();   // status is never on stdout
    }

    [Fact]
    public void ToolStatus_Suppressed_WhenStatusHidden()   // piped reply (stdout not a TTY) stays clean
    {
        var (renderer, _, err) = Make(showStatus: false, color: false);

        renderer.Handle(AssistantStreamEvent.ToolStart(new Tool("get_status"), new Dictionary<string, string?>()));
        renderer.Handle(AssistantStreamEvent.ToolResult(new Tool("get_status"), "ok"));

        err.ToString().Should().BeEmpty();
    }

    [Fact]
    public void Confirmation_IsCollected_NotPrinted()
    {
        var (renderer, @out, err) = Make(showStatus: true, color: false);

        renderer.Handle(AssistantStreamEvent.Confirmation(new PendingConfirmation(ConfirmationKind.Uninstall, "terraria")));

        renderer.Confirmations.Should().ContainSingle().Which.Target.Should().Be("terraria");
        @out.ToString().Should().BeEmpty();
        err.ToString().Should().BeEmpty();
    }

    [Fact]
    public void Error_WritesToStderr_AndFlagsHadError()
    {
        var (renderer, _, err) = Make(showStatus: true, color: false);

        renderer.Handle(AssistantStreamEvent.Error("ollama unreachable"));

        renderer.HadError.Should().BeTrue();
        err.ToString().Should().Contain("error: ollama unreachable");
    }

    [Fact]
    public void Final_WithoutAnyTokens_PrintsTheText()
    {
        var (renderer, @out, _) = Make(showStatus: true, color: false);

        renderer.Handle(AssistantStreamEvent.Final("there are no servers"));

        @out.ToString().Should().Be("there are no servers" + Environment.NewLine);
    }

    [Fact]
    public void Color_WrapsStatusInAnsi_WhenEnabled()
    {
        var (renderer, _, err) = Make(showStatus: true, color: true);

        renderer.Handle(AssistantStreamEvent.ToolStart(new Tool("x"), new Dictionary<string, string?>()));

        err.ToString().Should().Contain("\x1b[2m").And.Contain("\x1b[0m");
    }

    [Fact]
    public void NoColor_LeavesStatusPlain()
    {
        var (renderer, _, err) = Make(showStatus: true, color: false);

        renderer.Handle(AssistantStreamEvent.Error("boom"));

        err.ToString().Should().NotContain("\x1b[");
    }

    [Fact]
    public void Final_WithUsage_PrintsContextLineInTokens_OnStderr_WhenStatusShown()
    {
        var (renderer, @out, err) = Make(showStatus: true, color: false);

        renderer.Handle(AssistantStreamEvent.Token("hi"));
        renderer.Handle(AssistantStreamEvent.Final("hi", new LlmUsage(1720, 130, 32768)));

        // Tokens, not a percentage: used (1850), window (32768), free (30918) — culture-agnostic
        // substring checks so a thousands-separator difference doesn't break the assertion.
        var stderr = err.ToString();
        stderr.Should().Contain("context:").And.Contain("tokens").And.Contain("free");
        stderr.Should().Contain("850").And.Contain("768");   // 1,850 used / 32,768 window
        @out.ToString().Should().Be("hi" + Environment.NewLine);   // usage never leaks to stdout
    }

    [Fact]
    public void Final_WithUsage_Suppressed_WhenStatusHidden()   // piped reply (stdout not a TTY) stays clean
    {
        var (renderer, _, err) = Make(showStatus: false, color: false);

        renderer.Handle(AssistantStreamEvent.Final("hi", new LlmUsage(1720, 130, 32768)));

        err.ToString().Should().BeEmpty();
    }

    [Fact]
    public void Final_WithoutUsage_PrintsNoContextLine()
    {
        var (renderer, _, err) = Make(showStatus: true, color: false);

        renderer.Handle(AssistantStreamEvent.Final("hi"));   // no usage reported

        err.ToString().Should().BeEmpty();
    }

    [Fact]
    public void StyledReply_RendersMarkdownAsAnsi_OnStdout()   // TTY + color → bold / inline code
    {
        var (renderer, @out, _) = Make(showStatus: true, color: true);

        renderer.Handle(AssistantStreamEvent.Token("Run **now** with `go`"));
        renderer.Handle(AssistantStreamEvent.Final("Run **now** with `go`"));

        var s = @out.ToString();
        s.Should().Contain("\x1b[1mnow\x1b[22m");    // **now** → bold, markers stripped
        s.Should().Contain("\x1b[36mgo\x1b[39m");     // `go` → inline code color
        s.Should().NotContain("**");
        s.Should().EndWith(Environment.NewLine);
    }

    [Fact]
    public void PipedReply_StaysRawMarkdown_NoAnsi()   // not a TTY → byte-for-byte passthrough
    {
        var (renderer, @out, _) = Make(showStatus: false, color: false);

        renderer.Handle(AssistantStreamEvent.Token("Run **now**"));
        renderer.Handle(AssistantStreamEvent.Final("Run **now**"));

        @out.ToString().Should().Be("Run **now**" + Environment.NewLine);
    }

    [Fact]
    public void Thinking_StreamsToStderr_AsOneBlock_NotOneFragmentPerLine()
    {
        var (renderer, @out, err) = Make(showStatus: true, color: false, showThinking: true);

        // Thinking arrives as token fragments (the parser returns them verbatim). They must flow
        // together, NOT print one fragment per line — the bug this fix exists to kill.
        renderer.Handle(AssistantStreamEvent.Thinking("The"));
        renderer.Handle(AssistantStreamEvent.Thinking(" user"));
        renderer.Handle(AssistantStreamEvent.Thinking(" wants"));

        var stderr = err.ToString();
        stderr.Should().Contain("thinking…");                              // one-time header
        stderr.Should().Contain("The user wants");                            // deltas flow together…
        stderr.Should().NotContain("The" + Environment.NewLine + " user");    // …not newline-per-delta
        stderr.Split("thinking").Length.Should().Be(2);                    // header printed exactly once
        @out.ToString().Should().BeEmpty();                                   // reasoning never on stdout
    }

    [Fact]
    public void Thinking_IsClosedBeforeReply_WithBlankSeparator_ReplyStaysCleanOnStdout()
    {
        var (renderer, @out, err) = Make(showStatus: true, color: false, showThinking: true);

        renderer.Handle(AssistantStreamEvent.Thinking("reasoning"));
        renderer.Handle(AssistantStreamEvent.Token("answer"));
        renderer.Handle(AssistantStreamEvent.Final("answer"));

        var nl = Environment.NewLine;
        err.ToString().Should().Contain("reasoning" + nl + nl);   // terminated + blank separator
        @out.ToString().Should().Be("answer" + nl);               // reply clean — no reasoning leak
    }

    [Fact]
    public void Thinking_Suppressed_WhenStderrNotInteractive()   // 2>/dev/null (or 2>file) drops reasoning
    {
        var (renderer, _, err) = Make(showStatus: true, color: false, showThinking: false);

        renderer.Handle(AssistantStreamEvent.Thinking("secret reasoning"));
        renderer.Handle(AssistantStreamEvent.Final("answer"));

        err.ToString().Should().BeEmpty();
    }

    [Fact]
    public void Thinking_IsDim_WhenColorEnabled()
    {
        var (renderer, _, err) = Make(showStatus: true, color: true, showThinking: true);

        renderer.Handle(AssistantStreamEvent.Thinking("hmm"));

        err.ToString().Should().Contain("\x1b[2m");   // dim SGR
    }
}
