using TheKrystalShip.KGSM.Speech;

namespace TheKrystalShip.Kgsm.Assistant.Service.Speech;

/// <summary>
/// The host's speech engine, as this service reaches it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The kgsm-speech leaf, over its socket.</b> Connecting is what starts it — the daemon is
/// socket-activated — so this service never launches a process, supervises one, or knows whether the
/// models are loaded. A daemon that idled out is replaced by the next sentence, transparently.
/// </para>
/// <para>
/// <b>No voice is named.</b> The engine speaks in the host's voice, which is what makes a person hear
/// the same assistant in a browser as they do in Discord. Naming one here would be this service
/// holding a second opinion about how the host sounds.
/// </para>
/// <para>
/// <b>WAV, because the listener is a browser.</b> A container is what <c>decodeAudioData</c> needs,
/// and asking for one costs the daemon a 44-byte header rather than an encode. It is not the smallest
/// thing that could travel — see the leaf's own notes on that trade.
/// </para>
/// </remarks>
internal sealed class LeafSpokenAudio : ISpokenAudio, IDisposable
{
    private readonly SpeechClient _client;
    private readonly ILogger<LeafSpokenAudio> _logger;

    public LeafSpokenAudio(string? socketPath, ILogger<LeafSpokenAudio> logger)
    {
        _logger = logger;
        _client = new SpeechClient(socketPath, logger);
    }

    public bool Available => _client.IsProvisioned;

    public string Mime => "audio/wav";

    public async Task<byte[]?> SayAsync(string text, CancellationToken ct = default)
    {
        if (!Available || string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            return await _client.SynthesizeAsync(text, SpeechProtocol.Format.Wav, voice: null, ct);
        }
        catch (Exception ex)
        {
            // One sentence not being spoken is not the turn failing: the words are already on their way
            // to the reader as text, and the next sentence gets its own attempt.
            _logger.LogDebug(ex, "Speech: could not synthesise a sentence");
            return null;
        }
    }

    public void Dispose() => _client.Dispose();
}
