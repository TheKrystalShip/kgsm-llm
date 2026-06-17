using FluentAssertions;

namespace TheKrystalShip.Kgsm.Assistant.Cli.Tests;

/// <summary>
/// The event→terminal mapping (§4), driven directly with scripted <see cref="AssistantStreamEvent"/>s
/// and captured <see cref="StringWriter"/>s. (AssistantStreamEvent / PendingConfirmation /
/// ConfirmationKind resolve via the enclosing TheKrystalShip.Kgsm.Assistant namespace.)
/// </summary>
public class TerminalRendererTests
{
    private static (TerminalRenderer renderer, StringWriter @out, StringWriter err) Make(bool showStatus, bool color)
    {
        var @out = new StringWriter();
        var err = new StringWriter();
        return (new TerminalRenderer(@out, err, showStatus, color), @out, err);
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
            "get_status", new Dictionary<string, string?> { ["instance"] = "terraria" }));

        err.ToString().Should().Contain("⚙ get_status(instance=terraria)");
        @out.ToString().Should().BeEmpty();   // status is never on stdout
    }

    [Fact]
    public void ToolStatus_Suppressed_WhenStatusHidden()   // piped reply (stdout not a TTY) stays clean
    {
        var (renderer, _, err) = Make(showStatus: false, color: false);

        renderer.Handle(AssistantStreamEvent.ToolStart("get_status", new Dictionary<string, string?>()));
        renderer.Handle(AssistantStreamEvent.ToolResult("get_status", "ok"));

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

        renderer.Handle(AssistantStreamEvent.ToolStart("x", new Dictionary<string, string?>()));

        err.ToString().Should().Contain("\x1b[2m").And.Contain("\x1b[0m");
    }

    [Fact]
    public void NoColor_LeavesStatusPlain()
    {
        var (renderer, _, err) = Make(showStatus: true, color: false);

        renderer.Handle(AssistantStreamEvent.Error("boom"));

        err.ToString().Should().NotContain("\x1b[");
    }
}
