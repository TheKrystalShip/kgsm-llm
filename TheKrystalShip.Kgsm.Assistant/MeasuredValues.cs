using System.Text;

namespace TheKrystalShip.Kgsm.Assistant;

/// <summary>
/// Ambient, per-turn record of every figure the turn was actually given — the tool output it read,
/// plus what the person asked and the lists the prompt injected.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <see cref="SearchIntent"/>'s ambient-scope shape, for the same reason: the text is
/// produced deep inside tool dispatch and needed by the reply review, with nothing between the two
/// to thread it through.
/// </para>
/// <para>
/// <b>The ledger is a holder written through, never an <see cref="AsyncLocal{T}"/> reassigned from
/// inside dispatch.</b> A value published from that depth does not propagate back out to the flow
/// that reads it — the same trap <see cref="SearchIntent"/> documents. The instance is published once
/// when the turn opens, and every note after that mutates it in place.
/// </para>
/// <para>
/// This is a haystack, not a parse. It exists to answer one question — was this figure in front of
/// the model, anywhere — so it deliberately keeps raw text rather than extracting typed values:
/// anything that tries to know what a number MEANS is a second, wronger copy of what the tools
/// already said.
/// </para>
/// </remarks>
public static class MeasuredValues
{
    private static readonly AsyncLocal<Ledger?> Current = new();

    private sealed class Ledger
    {
        public readonly StringBuilder Text = new();

        public bool AnyToolReported;
    }

    /// <summary>
    /// Everything the model was given this turn, concatenated. Empty outside a turn, which reads as
    /// "nothing is known to have been given" and is what makes the review fail open rather than
    /// flagging every figure in the reply.
    /// </summary>
    public static string Given => Current.Value?.Text.ToString() ?? string.Empty;

    /// <summary>Whether a turn is open — the review asks, so that it stays quiet outside one.</summary>
    public static bool IsOpen => Current.Value is not null;

    /// <summary>
    /// Whether any tool reported anything this turn. The review checks it because a turn that called
    /// no tool is answering from the model's own knowledge, where stating a well-known default port is
    /// a fair answer rather than a misquoted measurement.
    /// </summary>
    public static bool AnyToolReported => Current.Value?.AnyToolReported ?? false;

    /// <summary>
    /// Opens a scope for one turn, seeded with what the model can legitimately quote before any tool
    /// runs: the request itself and the system prompt, which carries the instance and blueprint lists
    /// and the host's clock.
    /// </summary>
    public static IDisposable BeginTurn(params string?[] seed)
    {
        var ledger = new Ledger();
        foreach (var part in seed)
            Append(ledger, part);

        Current.Value = ledger;
        return new Turn();
    }

    /// <summary>Records what a tool just reported. A no-op outside a turn.</summary>
    public static void Note(string? toolOutput)
    {
        if (Current.Value is not { } ledger)
            return;

        // Marked even for an empty answer: the tool ran, and what it reported — including nothing —
        // is what the reply has to be built from.
        ledger.AnyToolReported = true;
        Append(ledger, toolOutput);
    }

    private static void Append(Ledger ledger, string? text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        ledger.Text.Append(text).Append('\n');
    }

    private sealed class Turn : IDisposable
    {
        public void Dispose() => Current.Value = null;
    }
}
