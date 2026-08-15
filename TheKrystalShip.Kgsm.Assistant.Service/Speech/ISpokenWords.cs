namespace TheKrystalShip.Kgsm.Assistant.Service.Speech;

/// <summary>
/// Turns an utterance somebody recorded into the words they said.
/// </summary>
/// <remarks>
/// <para>
/// <b>The other direction of <see cref="ISpokenAudio"/>, and an enhancement on the same terms.</b>
/// Recognition lives in the same leaf as synthesis, so a host has both or neither, and a host with
/// neither is the ordinary case — this port has a null implementation that is always registered, and
/// a surface with nowhere to send audio asks people to type instead.
/// </para>
/// <para>
/// <b>Whole utterances, not a stream.</b> A voice note is recorded, ended deliberately, and sent — the
/// person is not talking to a live microphone that has to work out where their sentence stopped. That
/// is what a Discord channel needs, and it is the surface's problem there rather than this one's.
/// </para>
/// </remarks>
internal interface ISpokenWords
{
    /// <summary>Whether this host has anything to listen with.</summary>
    /// <remarks>
    /// Answered without starting or loading anything: it is the question "is kgsm-speech installed
    /// here", asked before a surface offers a microphone at all.
    /// </remarks>
    bool Available { get; }

    /// <summary>
    /// Reads one utterance — 16kHz mono signed 16-bit PCM, whisper's native input.
    /// </summary>
    /// <remarks>
    /// <b>Three outcomes, and two of them are not failures.</b> Words are words. An <b>empty</b> string
    /// is a pass that ran and found nothing recognisable, which is what a recording of a quiet room is.
    /// <see langword="null"/> is the pass not happening — no engine, or one that could not answer.
    /// A caller that flattens the last two tells somebody they said nothing when the truth is that
    /// nobody listened.
    /// </remarks>
    Task<string?> HearAsync(byte[] pcm16k, CancellationToken ct = default);
}

/// <summary>
/// What an assistant with no speech engine hears. Registered first and always.
/// </summary>
internal sealed class NoSpokenWords : ISpokenWords
{
    public bool Available => false;

    public Task<string?> HearAsync(byte[] pcm16k, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);
}
