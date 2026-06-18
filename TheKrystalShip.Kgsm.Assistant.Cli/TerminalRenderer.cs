using System.Text;

namespace TheKrystalShip.Kgsm.Assistant.Cli;

/// <summary>
/// The CLI analogue of the service's <c>SseTurnWriter</c>: consumes the assistant's
/// <see cref="AssistantStreamEvent"/> stream and maps each event to the terminal.
/// <list type="bullet">
///   <item><c>Token</c> → the reply, streamed to <b>stdout</b> with no trailing newline.</item>
///   <item><c>Thinking</c> → the model's reasoning, streamed dim to <b>stderr</b> under a one-time
///   <c>💭 thinking…</c> header (no per-delta newline — the fragments flow as one block), gated on
///   stderr being a TTY so <c>2&gt;/dev/null</c> drops it. Any later event closes the block with a
///   blank separator, so the reasoning never runs into the reply.</item>
///   <item><c>ToolStart</c>/<c>ToolResult</c> → a dim status line on <b>stderr</b>, only when
///   stdout is a TTY (so a redirected/piped reply stays clean — that is the §4 contract:
///   <c>assistant "…" | cat</c> emits the reply and nothing else).</item>
///   <item><c>Confirmation</c> → <b>collected</b> (drained by the confirmation flow), never printed here.</item>
///   <item><c>Final</c> → a single trailing newline (the tokens already carried the text).</item>
///   <item><c>Error</c> → a red line on <b>stderr</b> (always — errors surface even when piped).</item>
/// </list>
/// Writers are injected so a test can capture output; the host passes <see cref="Console.Out"/> /
/// <see cref="Console.Error"/>.
/// </summary>
internal sealed class TerminalRenderer
{
    private readonly TextWriter _out;
    private readonly TextWriter _err;
    private readonly bool _showStatus;     // stdout is interactive (a TTY)
    private readonly bool _color;          // stderr is interactive AND color not disabled
    private readonly bool _showThinking;   // stderr is interactive (a TTY) — gate for streamed reasoning
    private readonly MarkdownStreamRenderer _md;
    private readonly List<PendingConfirmation> _confirmations = new();
    private bool _wroteToken;
    private bool _inThinking;   // a reasoning block is open (header written, deltas streaming, not yet closed)

    public TerminalRenderer(
        TextWriter outWriter, TextWriter errWriter, bool showStatus, bool color, bool showThinking)
    {
        _out = outWriter;
        _err = errWriter;
        _showStatus = showStatus;
        _color = color;
        _showThinking = showThinking;
        // The reply (stdout) is styled only when stdout is a TTY AND color is on; otherwise the
        // markdown renderer is a byte-for-byte passthrough, so a piped reply stays exactly the
        // model's raw text — the §4 pipe-clean contract.
        _md = new MarkdownStreamRenderer(outWriter, showStatus && color);
    }

    /// <summary>Confirmations staged during the turn, in arrival order (drained after the stream ends).</summary>
    public IReadOnlyList<PendingConfirmation> Confirmations => _confirmations;

    /// <summary>True if the turn ended in an <c>Error</c> event.</summary>
    public bool HadError { get; private set; }

    /// <summary>Maps one streamed event to terminal output.</summary>
    public void Handle(AssistantStreamEvent ev)
    {
        // Any non-thinking event closes an open reasoning block first, so the streamed thinking is
        // terminated and separated before the reply, a tool line, or an error follows it.
        if (ev.Kind != AssistantEventKind.Thinking)
            CloseThinking();

        switch (ev.Kind)
        {
            case AssistantEventKind.Token:
                if (!string.IsNullOrEmpty(ev.Text))
                {
                    _md.Write(ev.Text);
                    _out.Flush();
                    _wroteToken = true;
                }
                break;

            case AssistantEventKind.Thinking:
                // Stream the reasoning to stderr as it generates: a one-time dim header, then the
                // deltas verbatim with NO per-delta newline so token fragments flow as one block
                // (the reply path does the same via _md.Write). Gated on stderr being a TTY.
                if (_showThinking && !string.IsNullOrEmpty(ev.Text))
                {
                    if (!_inThinking)
                    {
                        _err.Write(Ansi.Paint("💭 thinking…", Ansi.Dim, _color));
                        _err.Write(Environment.NewLine);
                        _inThinking = true;
                    }
                    _err.Write(Ansi.Paint(ev.Text, Ansi.Dim, _color));
                    _err.Flush();
                }
                break;

            case AssistantEventKind.ToolStart:
                if (_showStatus)
                    _err.WriteLine(
                        Ansi.Paint("⚙ ", Ansi.Dim, _color)
                        + Ansi.Paint(ev.ToolName?.Name ?? string.Empty, Ansi.Cyan, _color)
                        + Ansi.Paint($"({FormatArgs(ev.ToolArguments)})", Ansi.Dim, _color));
                break;

            case AssistantEventKind.ToolResult:
                if (_showStatus)
                    _err.WriteLine(
                        Ansi.Paint("✓ ", Ansi.Green, _color)
                        + Ansi.Paint(ev.ToolName?.Name ?? string.Empty, Ansi.Dim, _color));
                break;

            case AssistantEventKind.Confirmation:
                if (ev.StagedConfirmation is not null)
                    _confirmations.Add(ev.StagedConfirmation);
                break;

            case AssistantEventKind.Final:
                // Tokens already streamed the reply; flush any carried markdown + close styles, then
                // close the line. If the turn produced no tokens but Final still carries text (e.g. a
                // pure tool turn), render that once through the same markdown path.
                if (_wroteToken)
                {
                    _md.Complete();
                    _out.WriteLine();
                }
                else if (!string.IsNullOrEmpty(ev.Text))
                {
                    _md.Write(ev.Text);
                    _md.Complete();
                    _out.WriteLine();
                }
                else
                {
                    _md.Complete();
                }
                _out.Flush();
                // Context occupancy in tokens (used / window), to stderr, TTY-only so a piped
                // reply stays clean — same discipline as the tool-status lines.
                if (_showStatus && ev.Usage is { } usage)
                    Status($"context: {usage.UsedTokens:N0} / {usage.ContextWindow:N0} tokens "
                        + $"({usage.RemainingTokens:N0} free)");
                break;

            case AssistantEventKind.Error:
                HadError = true;
                // If a partial reply was already streaming, flush + close styles, then break the
                // line before the error (so an interrupted reply never leaves the terminal bold).
                _md.Complete();
                if (_wroteToken)
                    _out.WriteLine();
                _out.Flush();
                var message = string.IsNullOrWhiteSpace(ev.ErrorMessage) ? "the assistant failed." : ev.ErrorMessage;
                _err.WriteLine(Ansi.Paint($"error: {message}", Ansi.Red, _color));
                break;
        }
    }

    /// <summary>
    /// Closes an open reasoning block: terminate the streamed line and add a blank separator before
    /// whatever follows (the reply, a tool line, the error). No-op when no block is open.
    /// </summary>
    private void CloseThinking()
    {
        if (!_inThinking)
            return;
        _err.Write(Environment.NewLine);   // end the streamed reasoning line
        _err.Write(Environment.NewLine);   // blank separator before the reply
        _err.Flush();
        _inThinking = false;
    }

    private void Status(string text) => _err.WriteLine(Ansi.Paint(text, Ansi.Dim, _color));

    /// <summary>Renders tool arguments as <c>k=v, k=v</c>, trimming long values so a status line stays one line.</summary>
    private static string FormatArgs(IReadOnlyDictionary<string, string?>? args)
    {
        if (args is null || args.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        var first = true;
        foreach (var (key, value) in args)
        {
            if (!first) sb.Append(", ");
            first = false;
            sb.Append(key).Append('=').Append(Trim(value));
        }
        return sb.ToString();
    }

    private static string Trim(string? value)
    {
        var v = value ?? string.Empty;
        return v.Length <= 40 ? v : v[..39] + "…";
    }
}
