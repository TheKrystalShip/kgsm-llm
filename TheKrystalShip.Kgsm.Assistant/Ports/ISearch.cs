namespace TheKrystalShip.Kgsm.Assistant.Ports;

/// <summary>
/// The unified knowledge-search capability the model sees as the single <c>search</c> tool: the
/// operator's local indexed docs first, the public web as a fallback (plan §3.4). A deterministic
/// composer over <see cref="IRetrieval"/> + <see cref="IWebSearch"/> with NO nested model calls.
/// Returns ready-to-use grounding text — including honest "nothing found" / "couldn't search"
/// messages — and never throws (the underlying ports don't either).
/// </summary>
public interface ISearch
{
    Task<string> SearchAsync(string query, CancellationToken cancellationToken = default);
}
