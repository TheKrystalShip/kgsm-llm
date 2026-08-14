namespace TheKrystalShip.Kgsm.Assistant.Service.Speech;

/// <summary>
/// Turns a sentence of an answer into audio a browser can play.
/// </summary>
/// <remarks>
/// <para>
/// <b>An enhancement, never a dependency.</b> Speech lives in its own leaf on the host, and a host
/// without it is the ordinary case — this port has a null implementation that is always registered,
/// so an assistant with no speech engine simply never emits an audio frame and every surface reads
/// the answer instead. It is the same shape as <c>DisabledRetrieval</c>: the real adapter registers
/// after the null one and wins only when configured.
/// </para>
/// <para>
/// <b>One sentence at a time, on purpose.</b> A whole answer synthesised at the end would arrive
/// after the reader had finished reading it. Sentence-sized is what lets the first one play while the
/// model is still writing the third.
/// </para>
/// </remarks>
internal interface ISpokenAudio
{
    /// <summary>Whether this host has anything to synthesise with.</summary>
    /// <remarks>
    /// Answered without starting or loading anything, because it is asked before every turn that might
    /// be spoken — a question about whether the leaf is installed, not whether it is warm.
    /// </remarks>
    bool Available { get; }

    /// <summary>
    /// Says <paramref name="text"/>, as a self-contained audio file. Null when it could not be said.
    /// </summary>
    /// <remarks>
    /// Self-contained rather than a stream of samples: each sentence is played on its own by the
    /// surface that receives it, and a container is what makes that possible with no decoder state
    /// carried between frames.
    /// </remarks>
    Task<byte[]?> SayAsync(string text, CancellationToken ct = default);

    /// <summary>The media type of what <see cref="SayAsync"/> returns, for the frame that carries it.</summary>
    string Mime { get; }
}

/// <summary>
/// What an assistant with no speech engine has. Registered first and always.
/// </summary>
/// <remarks>
/// Reporting unavailable rather than throwing is the whole point: every caller treats audio as
/// something that may not happen, so there is one path for "this host cannot speak" and it is the
/// path a host with no leaf takes on every turn.
/// </remarks>
internal sealed class NoSpokenAudio : ISpokenAudio
{
    public bool Available => false;

    public string Mime => string.Empty;

    public Task<byte[]?> SayAsync(string text, CancellationToken ct = default) =>
        Task.FromResult<byte[]?>(null);
}
