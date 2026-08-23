using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.KGSM.Speech;

namespace TheKrystalShip.Kgsm.Assistant.Service.Speech;

/// <summary>
/// The host's speech engine, listening.
/// </summary>
/// <remarks>
/// <para>
/// <b>The kgsm-speech leaf, over the same socket the synthesiser uses.</b> Connecting is what starts
/// it, so this service never launches a process and never knows whether the models are loaded.
/// </para>
/// <para>
/// <b>Primed with this host's names.</b> A recogniser knows English, not that a server here is called
/// <c>Ketchup</c> — it spells that "catch-up", which is a correct reading of the sound and the wrong
/// answer. <see cref="SpokenVocabulary"/> writes the names down and every surface on the host sends
/// the same ones; what this owns is reading the inventory and how often. Nothing downstream rewrites
/// a transcript: correcting a misheard name after the fact means guessing which server somebody meant
/// and then acting on the guess.
/// </para>
/// <para>
/// <b>An empty transcript is a real answer.</b> A voice note of a quiet room recognises to nothing,
/// and that is worth reporting as itself rather than as a failure — the person pressed record and
/// said nothing, which they can see and fix.
/// </para>
/// </remarks>
internal sealed class LeafSpokenWords : ISpokenWords, IDisposable
{
    /// <summary>
    /// How long the names are trusted before the inventory is read again.
    /// </summary>
    /// <remarks>
    /// A server installed is not heard about for up to this long, which costs one misheard name. The
    /// read is a cache hit almost always — the inventory has its own TTL underneath — so this exists
    /// to bound the case where it is not.
    /// </remarks>
    private static readonly TimeSpan VocabularyInterval = TimeSpan.FromMinutes(2);

    private readonly SpeechClient _client;
    private readonly IServerInventory _inventory;
    private readonly ILogger<LeafSpokenWords> _logger;

    private string _vocabulary = string.Empty;
    private DateTimeOffset _vocabularyCheckedAt = DateTimeOffset.MinValue;

    public LeafSpokenWords(
        string? socketPath, IServerInventory inventory, ILogger<LeafSpokenWords> logger)
    {
        _inventory = inventory;
        _logger = logger;
        _client = new SpeechClient(socketPath, logger);
    }

    public bool Available => _client.IsProvisioned;

    public async Task<string?> HearAsync(byte[] pcm16k, CancellationToken ct = default)
    {
        if (!Available || pcm16k.Length == 0) return null;

        string vocabulary = await PrimingAsync(ct);

        try
        {
            (SpeechProtocol.Outcome outcome, string text) =
                await _client.TranscribeAsync(pcm16k, vocabulary, ifIdle: false, ct);

            if (outcome != SpeechProtocol.Outcome.Done) return null;

            string transcript = text.Trim();

            // Given audio with nothing recognisable in it, whisper sometimes continues the context it
            // was primed with instead of returning nothing. That arrives here looking exactly like
            // speech, and a run of this host's server names is a plausible enough request to be worth
            // answering — so it is caught by what it is and reported as an empty room.
            if (SpokenVocabulary.IsEchoOf(transcript, vocabulary))
            {
                _logger.LogDebug("Speech: discarded a transcript that was the primed vocabulary coming back");
                return string.Empty;
            }

            return transcript;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Speech: could not recognise an utterance");
            return null;
        }
    }

    /// <summary>
    /// The names to expect, kept in step with what is installed.
    /// </summary>
    /// <remarks>
    /// An inventory that cannot be read leaves the previous names standing rather than clearing them.
    /// The names did not stop being the names because the engine could not be asked, and a recogniser
    /// that forgets them mid-outage starts mishearing every server at the moment somebody most needs
    /// to ask about one.
    /// </remarks>
    private async Task<string> PrimingAsync(CancellationToken ct)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now - _vocabularyCheckedAt < VocabularyInterval) return _vocabulary;
        _vocabularyCheckedAt = now;

        try
        {
            IReadOnlyDictionary<string, string> instances = await _inventory.GetInstanceLabelsAsync(ct);
            IReadOnlyCollection<string> blueprints = await _inventory.GetBlueprintNamesAsync(ct);

            // Both names of every server: somebody says the label out loud and the id is what the
            // engine calls it, and a recogniser primed with only one of them mishears the other.
            IReadOnlyCollection<string> spoken =
                [.. instances.Keys.Concat(instances.Values).Distinct(StringComparer.OrdinalIgnoreCase)];

            // No trigger phrase: somebody pressed a button to start recording, so there is no wake word
            // in the audio and naming one would prime the recogniser to hear one that was never said.
            string composed = SpokenVocabulary.Compose([], spoken, blueprints);

            if (composed != _vocabulary)
            {
                _vocabulary = composed;
                _logger.LogInformation(
                    "Speech: priming the recogniser with {Characters} characters of this host's names",
                    composed.Length);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Speech: could not read the inventory to prime the recogniser");
        }

        return _vocabulary;
    }

    public void Dispose() => _client.Dispose();
}
