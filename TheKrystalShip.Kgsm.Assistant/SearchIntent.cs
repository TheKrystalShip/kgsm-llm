using TheKrystalShip.Kgsm.Assistant.Ports;

namespace TheKrystalShip.Kgsm.Assistant;

/// <summary>
/// Ambient, per-turn record of where the person asking said to look.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <see cref="ITurnProgress"/>'s and <see cref="IConfirmationContext"/>'s ambient-scope shape,
/// for the same reason: the fact is known when the turn starts and needed deep inside tool dispatch,
/// with nothing between the two to thread it through. Backed by an <see cref="AsyncLocal{T}"/>, so
/// concurrent turns stay isolated.
/// </para>
/// <para>
/// ⚠ <b>What is set here beats what the model asked for.</b> A scope on the tool call is the model's
/// reading of the request; this is the request. When they disagree the person wins — that is the whole
/// point of recording it, since the measured failure was the model quietly declining to set the scope
/// at all.
/// </para>
/// <para>
/// Nothing is set on an ordinary turn, and then this is <see cref="SearchScope.Auto"/> and the search
/// behaves exactly as it always has.
/// </para>
/// </remarks>
public static class SearchIntent
{
    private static readonly AsyncLocal<SearchScope?> Current = new();

    /// <summary>
    /// Whether a search has actually run this turn.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Forcing the scope was still not enough on its own.</b> Where the model previously called
    /// <c>search</c> without a scope, it was measured going one step further and not calling it at
    /// all — answering "look it up online" out of its own parametric knowledge, with no tool call to
    /// apply a scope to. Recording that a search happened is what lets the reply review notice the
    /// difference between looking something up and appearing to.
    /// <para>
    /// A holder rather than a bare bool, because an <see cref="AsyncLocal{T}"/> value type set deep
    /// inside tool dispatch does not propagate back out to the flow that reads it. The instance is
    /// published once when the turn opens and written through afterwards.
    /// </para>
    /// </remarks>
    private static readonly AsyncLocal<Ran?> Searched = new();

    private sealed class Ran
    {
        public bool Yes;
    }

    /// <summary>Where this turn's searches must look, or null when the person did not say.</summary>
    public static SearchScope? Required => Current.Value;

    /// <summary>Whether anything was actually searched this turn.</summary>
    public static bool AnythingSearched => Searched.Value?.Yes ?? false;

    /// <summary>Records that a search ran. A no-op outside a turn.</summary>
    public static void NoteSearched()
    {
        if (Searched.Value is { } ran) ran.Yes = true;
    }

    /// <summary>Opens a scope for one turn. Dispose to clear it.</summary>
    public static IDisposable BeginTurn(SearchScope? scope)
    {
        Current.Value = scope;
        Searched.Value = new Ran();
        return new Turn();
    }

    /// <summary>
    /// Reads the turn's request straight off what somebody typed.
    /// </summary>
    /// <remarks>
    /// Only ever returns <see cref="SearchScope.Web"/> or null: there is no phrase a person uses to
    /// confine the assistant to documentation they cannot see, so nothing here narrows a search.
    /// </remarks>
    public static SearchScope? From(string? userPrompt) =>
        AskedForTheWeb.In(userPrompt) ? SearchScope.Web : null;

    private sealed class Turn : IDisposable
    {
        public void Dispose()
        {
            Current.Value = null;
            Searched.Value = null;
        }
    }
}
