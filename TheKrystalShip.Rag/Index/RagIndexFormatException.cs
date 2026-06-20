namespace TheKrystalShip.Rag.Index;

/// <summary>
/// Thrown when a file on disk is not a readable KRAG index of the current format version
/// (bad magic, or an unsupported <see cref="RagIndex.CurrentFormatVersion"/>).
/// <para>
/// The index is a <b>regenerable derived artifact</b>, never source-of-truth: callers treat
/// this exception as "discard and re-index from sources," never as a migration. That is why
/// the reader has no back-compat read paths — an old layout is rebuilt, not parsed (plan §D9).
/// </para>
/// </summary>
public sealed class RagIndexFormatException : Exception
{
    public RagIndexFormatException(string message) : base(message) { }
}
