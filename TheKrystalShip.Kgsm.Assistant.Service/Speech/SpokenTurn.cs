using TheKrystalShip.KGSM.Speech;

namespace TheKrystalShip.Kgsm.Assistant.Service.Speech;

/// <summary>
/// Reads a turn aloud while it is still being written.
/// </summary>
/// <remarks>
/// <para>
/// <b>It rides the token stream, not the finished answer.</b> Each sentence is synthesised the moment
/// it is complete, so the first one is playing while the model writes the third. Synthesising at the
/// end would deliver the audio after the reader had already read the text — the wait it exists to
/// remove.
/// </para>
/// <para>
/// <b>Sentences are spoken in the order they were written, and never overlap.</b> One request is in
/// flight at a time: the engine serialises them anyway (one card, one voice), and letting several run
/// would only reorder the answer. A sentence that fails to synthesise is skipped rather than retried
/// — the words are already in front of the reader as text.
/// </para>
/// <para>
/// <b>It never delays the turn.</b> Synthesis happens on its own task; the frames the reader is
/// receiving are not held up behind an engine that is busy or slow, and a turn that finishes while
/// audio is still being made simply keeps emitting until it catches up.
/// </para>
/// </remarks>
internal sealed class SpokenTurn : IAsyncDisposable
{
    private readonly ISpokenAudio _audio;
    private readonly Action<int, string, byte[]> _emit;
    private readonly ILogger _logger;
    private readonly CancellationToken _ct;

    private readonly SpokenSentences _sentences = new();
    private readonly SemaphoreSlim _one = new(1, 1);

    private Task _saying = Task.CompletedTask;
    private int _seq;

    public SpokenTurn(
        ISpokenAudio audio, Action<int, string, byte[]> emit, ILogger logger, CancellationToken ct)
    {
        _audio = audio;
        _emit = emit;
        _logger = logger;
        _ct = ct;
    }

    /// <summary>Takes one slice of the reply as it arrives.</summary>
    public void Take(string delta)
    {
        foreach (string sentence in _sentences.Take(delta))
            Say(sentence);
    }

    /// <summary>
    /// Says whatever is left and waits for the audio to catch up with the words.
    /// </summary>
    /// <remarks>
    /// Awaited so the frames are all emitted before the turn's consumers are completed — a surface
    /// that received <c>done</c> and then an audio frame would have to hold a finished turn open to
    /// use it.
    /// </remarks>
    public async Task FinishAsync()
    {
        string? last = _sentences.Flush();
        if (last is not null) Say(last);

        try
        {
            await _saying;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Speech: the last of a spoken turn did not finish");
        }
    }

    /// <summary>Queues one sentence behind whatever is already being said.</summary>
    private void Say(string sentence)
    {
        int seq = _seq++;

        _saying = _saying.ContinueWith(async _ =>
        {
            if (_ct.IsCancellationRequested) return;

            await _one.WaitAsync(_ct);
            try
            {
                byte[]? audio = await _audio.SayAsync(sentence, _ct);
                if (audio is null || audio.Length == 0) return;

                _emit(seq, _audio.Mime, audio);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                // One sentence lost is one sentence not heard; the reader has the whole answer in
                // text either way, and the next sentence gets its own attempt.
                _logger.LogDebug(ex, "Speech: could not say a sentence of a turn");
            }
            finally
            {
                _one.Release();
            }
        }, CancellationToken.None).Unwrap();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _saying;
        }
        catch
        {
            // Disposal reports nothing: whatever failed was already logged where it happened.
        }

        _one.Dispose();
    }
}
