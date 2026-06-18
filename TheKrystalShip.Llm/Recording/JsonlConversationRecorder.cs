using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;
using TheKrystalShip.Llm.Ollama;

namespace TheKrystalShip.Llm.Recording;

/// <summary>
/// Appends one JSON object per turn to a daily <c>yyyy-MM-dd.jsonl</c> file — a greppable,
/// jq-friendly corpus that can be fed straight to a model for analysis. Each <see cref="Record"/>
/// opens-appends-closes (self-flushing) so a one-shot CLI invocation's single turn survives an
/// immediate process exit; writes are serialized by a lock and failure-isolated (a corpus write
/// must never take down a user's turn).
/// </summary>
/// <remarks>
/// The per-process lock makes concurrent turns within one host safe. If the corpus is ever shared by
/// multiple processes (e.g. the HTTP service enabling recording alongside the CLI), upgrade the
/// append to an atomic O_APPEND line write — a cross-process lock is out of scope for the CLI-only V1.
/// </remarks>
public sealed class JsonlConversationRecorder : IConversationRecorder
{
    private static readonly JsonSerializerOptions Json = new()
    {
        // A local analysis file, never HTML: keep game names / glyphs readable rather than \uXXXX.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // Keep nulls — a cancelled turn's "final": null is meaningful signal, not noise.
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _model;
    private readonly double _temperature;
    private readonly string? _label;
    private readonly ILogger<JsonlConversationRecorder> _logger;

    public JsonlConversationRecorder(
        IOptions<RecordingOptions> recording,
        IOptions<OllamaOptions> ollama,
        ILogger<JsonlConversationRecorder> logger)
    {
        _directory = recording.Value.Directory;
        // Stamp each line with the producing model/params (read from the source of truth so they
        // can't drift) — lets analysis correlate quality with the model and sampling settings.
        _model = ollama.Value.Model;
        _temperature = ollama.Value.Temperature;
        _label = string.IsNullOrWhiteSpace(recording.Value.Label) ? null : recording.Value.Label.Trim();
        _logger = logger;
    }

    public bool Enabled => !string.IsNullOrWhiteSpace(_directory);

    public void Record(ConversationTurnRecord record)
    {
        try
        {
            var line = JsonSerializer.Serialize(ToWire(record), Json);
            var path = Path.Combine(_directory, $"{record.CompletedAt:yyyy-MM-dd}.jsonl");

            lock (_gate)
            {
                Directory.CreateDirectory(_directory);
                File.AppendAllText(path, line + "\n");
            }
        }
        catch (Exception ex)
        {
            // Best-effort: never let a transcript write fail the turn the user actually cared about.
            _logger.LogWarning(ex, "Failed to record conversation turn for {Conversation}", record.ConversationId);
        }
    }

    private object ToWire(ConversationTurnRecord r) => new
    {
        ts = r.CompletedAt,
        conv = r.ConversationId,
        durMs = (long)(r.CompletedAt - r.StartedAt).TotalMilliseconds,
        user = r.UserPrompt,
        tools = r.Tools.Select(t => new
        {
            name = t.Name.Name,
            args = t.Arguments,
            result = t.Result,
            ms = t.DurationMs,
        }),
        iters = r.Iterations,
        capHit = r.Outcome == TurnOutcome.CapHit,
        final = r.Final,
        usage = r.Usage is null ? null : new
        {
            prompt = r.Usage.PromptTokens,
            resp = r.Usage.ResponseTokens,
            ctx = r.Usage.ContextWindow,
        },
        model = _model,
        temp = _temperature,
        label = _label,
        sysHash = r.SystemPromptHash,
        outcome = r.Outcome switch
        {
            TurnOutcome.Ok => "ok",
            TurnOutcome.Error => "error",
            TurnOutcome.CapHit => "cap-hit",
            TurnOutcome.Cancelled => "cancelled",
            _ => "unknown",
        },
        error = r.Error,
    };
}
