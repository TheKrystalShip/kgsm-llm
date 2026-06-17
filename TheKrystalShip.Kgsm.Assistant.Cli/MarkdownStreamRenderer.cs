namespace TheKrystalShip.Kgsm.Assistant.Cli;

/// <summary>
/// Streams the assistant's reply to stdout, rendering a safe subset of Markdown as ANSI while the
/// tokens arrive. Terminal-only: it styles only when output is styled (stdout is a TTY and color is
/// enabled); otherwise it is a byte-for-byte passthrough, so a piped/redirected reply is exactly the
/// model's raw text and the §4 pipe-clean contract holds.
///
/// The subset is deliberately small and snake_case-safe — <b>no italic</b>, because a single
/// <c>*</c>/<c>_</c> marker would mangle identifiers like <c>instance_name</c>. It covers inline
/// <c>**bold**</c> and <c>`code`</c>, plus line-level ATX headers (<c># …</c>), bullets
/// (<c>-</c>/<c>*</c>/<c>+</c> then a space) and fenced <c>```</c> code blocks. Everything else is
/// emitted verbatim.
///
/// Rendering is incremental with a tiny carry buffer (<see cref="_pending"/>) that holds only the
/// trailing bytes which could still begin a multi-char marker (a lone <c>*</c>, a partial <c>```</c>
/// fence, a <c>#</c> run with no terminator yet, a bullet marker with no following char). That makes
/// the output independent of how the token stream is chunked — the property the tests pin down.
/// </summary>
internal sealed class MarkdownStreamRenderer
{
    // Fine-grained SGR: targeted on/off codes so styles compose without a blanket reset.
    private const string BoldOn = "\x1b[1m";
    private const string BoldOff = "\x1b[22m";   // normal intensity — clears bold (and dim)
    private const string CodeOn = "\x1b[36m";    // cyan
    private const string CodeOff = "\x1b[39m";   // default foreground
    private const string DimOn = "\x1b[2m";
    private const string DimOff = "\x1b[22m";
    private const string Bullet = "  \x1b[36m•\x1b[39m ";   // indent + cyan glyph + space

    private readonly TextWriter _out;
    private readonly bool _styled;

    private string _pending = string.Empty;
    private bool _atLineStart = true;
    private bool _inFence;
    private bool _skipToNewline;   // consuming a fence info-string ( ```bash ) up to and incl. its newline
    private bool _bold;
    private bool _code;
    private bool _header;

    public MarkdownStreamRenderer(TextWriter outWriter, bool styled)
    {
        _out = outWriter;
        _styled = styled;
    }

    /// <summary>Feeds one streamed reply delta.</summary>
    public void Write(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        if (!_styled)
        {
            _out.Write(text);   // passthrough: exact bytes, pipe-clean
            return;
        }
        _pending += text;
        Consume(flush: false);
    }

    /// <summary>
    /// Flushes any carried bytes and closes open styles. Does <b>not</b> emit a trailing newline —
    /// the caller (<see cref="TerminalRenderer"/>) owns the line ending.
    /// </summary>
    public void Complete()
    {
        if (!_styled)
            return;
        Consume(flush: true);
        CloseInline();
        if (_inFence)
        {
            _out.Write(DimOff);
            _inFence = false;
        }
    }

    private void CloseInline()
    {
        if (_code) { _out.Write(CodeOff); _code = false; }
        if (_bold || _header) { _out.Write(BoldOff); _bold = false; _header = false; }
    }

    private void Consume(bool flush)
    {
        while (_pending.Length > 0)
        {
            // 1. Consuming a fence info-string up to and including the newline.
            if (_skipToNewline)
            {
                var nl = _pending.IndexOf('\n');
                if (nl < 0)
                {
                    _pending = string.Empty;   // newline not seen yet; keep skipping on later feeds
                    return;
                }
                _pending = _pending[(nl + 1)..];
                _skipToNewline = false;
                _atLineStart = true;
                continue;
            }

            // 2. Inside a fenced code block: raw passthrough, detecting only a closing ``` at line start.
            if (_inFence)
            {
                if (_atLineStart)
                {
                    if (_pending[0] == '`')
                    {
                        if (_pending.Length < 3)
                        {
                            if (!flush) return;       // could still become a closing ```
                            _atLineStart = false;     // flush: it isn't → content
                        }
                        else if (_pending[1] == '`' && _pending[2] == '`')
                        {
                            _out.Write(DimOff);
                            _inFence = false;
                            _skipToNewline = true;
                            _pending = _pending[3..];
                            continue;
                        }
                        else
                        {
                            _atLineStart = false;     // backtick(s) but not ``` → content
                        }
                    }
                    else
                    {
                        _atLineStart = false;         // doesn't start with ` → content
                    }
                }

                var fc = _pending[0];
                _out.Write(fc);
                _pending = _pending[1..];
                if (fc == '\n')
                    _atLineStart = true;
                continue;
            }

            // 3. Line-start block markers: header / bullet / fence-open.
            if (_atLineStart)
            {
                var c0 = _pending[0];

                if (c0 == '`')
                {
                    if (_pending.Length < 3)
                    {
                        if (!flush) return;           // might become ```
                        _atLineStart = false;         // flush: not a fence → inline
                        continue;
                    }
                    if (_pending[1] == '`' && _pending[2] == '`')
                    {
                        _out.Write(DimOn);
                        _inFence = true;
                        _skipToNewline = true;
                        _pending = _pending[3..];
                        continue;
                    }
                    _atLineStart = false;             // single backtick at line start → inline code
                    continue;
                }

                if (c0 == '#')
                {
                    var n = 0;
                    while (n < _pending.Length && _pending[n] == '#') n++;
                    if (n == _pending.Length)
                    {
                        if (!flush) return;           // need the char after the # run
                        _out.Write(_pending);         // flush: bare #'s, literal
                        _pending = string.Empty;
                        _atLineStart = false;
                        continue;
                    }
                    if (n <= 6 && _pending[n] == ' ')
                    {
                        _out.Write(BoldOn);
                        _header = true;
                        _pending = _pending[(n + 1)..];
                        _atLineStart = false;
                        continue;
                    }
                    _out.Write(new string('#', n));   // not a header → literal #'s, then inline
                    _pending = _pending[n..];
                    _atLineStart = false;
                    continue;
                }

                if (c0 is '-' or '+' or '*')
                {
                    if (_pending.Length < 2)
                    {
                        if (!flush) return;           // need the char after the marker
                        if (c0 != '*') { _out.Write(c0); _pending = _pending[1..]; }
                        _atLineStart = false;         // '*' defers to inline (could be **)
                        continue;
                    }
                    if (_pending[1] == ' ')
                    {
                        _out.Write(Bullet);
                        _pending = _pending[2..];
                        _atLineStart = false;
                        continue;
                    }
                    _atLineStart = false;
                    if (c0 != '*') { _out.Write(c0); _pending = _pending[1..]; }
                    continue;                         // '*' → inline handles ** / literal
                }

                _atLineStart = false;                 // ordinary char → reprocess via inline
                continue;
            }

            // 4. Inline content.
            var c = _pending[0];

            if (c == '\n')
            {
                CloseInline();                        // styles never bleed across a line break
                _out.Write('\n');
                _pending = _pending[1..];
                _atLineStart = true;
                continue;
            }

            if (c == '*')
            {
                if (_pending.Length < 2)
                {
                    if (!flush) return;               // might become **
                    _out.Write('*');
                    _pending = string.Empty;
                    continue;
                }
                if (_pending[1] == '*')
                {
                    _out.Write(_bold ? BoldOff : BoldOn);
                    _bold = !_bold;
                    _pending = _pending[2..];
                    continue;
                }
                _out.Write('*');                      // single star → literal (no italic)
                _pending = _pending[1..];
                continue;
            }

            if (c == '`')
            {
                _out.Write(_code ? CodeOff : CodeOn);
                _code = !_code;
                _pending = _pending[1..];
                continue;
            }

            _out.Write(c);
            _pending = _pending[1..];
        }
    }
}
