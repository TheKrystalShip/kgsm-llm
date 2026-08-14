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

    /// <summary>Where this turn's searches must look, or null when the person did not say.</summary>
    public static SearchScope? Required => Current.Value;

    /// <summary>Opens a scope for one turn. Dispose to clear it.</summary>
    public static IDisposable BeginTurn(SearchScope? scope)
    {
        Current.Value = scope;
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
        public void Dispose() => Current.Value = null;
    }
}
