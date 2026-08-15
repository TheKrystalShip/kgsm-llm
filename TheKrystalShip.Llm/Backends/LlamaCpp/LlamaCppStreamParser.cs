using System.Text;
using System.Text.Json;

using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Llm.Backends.LlamaCpp;

/// <summary>
/// Pure, HTTP-free parsing of llama-server's OpenAI-compatible <c>/v1/chat/completions</c>
/// response. A buffered response is one JSON object; a streamed one is Server-Sent Events —
/// <c>data: {…}</c> lines closed by <c>data: [DONE]</c>.
/// <para>
/// Unlike Ollama's parser this one is <b>stateful</b>, and it has to be: the OpenAI format streams
/// a tool call's arguments as string fragments spread across frames, keyed by index, so nothing can
/// be decided from a single frame. The parser accumulates those fragments and emits the assembled
/// set once, in one frame, followed by a separate terminal frame — which is the shape the agent
/// loop already consumes from Ollama. One instance serves exactly one response.
/// </para>
/// </summary>
public sealed class LlamaCppStreamParser
{
    private const string DataPrefix = "data:";
    private const string DoneSentinel = "[DONE]";

    private readonly int _contextWindow;
    private readonly SortedDictionary<int, PartialToolCall> _partials = [];

    private LlmUsage? _usage;
    private bool _toolCallsEmitted;
    private bool _doneEmitted;

    public LlamaCppStreamParser(int contextWindow) => _contextWindow = contextWindow;

    /// <summary>
    /// Parses one SSE line. Returns null when the line carries nothing a consumer should see —
    /// a blank line, a comment, a field other than <c>data</c>, malformed JSON, or a frame whose
    /// only content was a tool-call fragment still being assembled.
    /// </summary>
    public LlmStreamChunk? ParseFrame(string sseLine)
    {
        if (string.IsNullOrWhiteSpace(sseLine))
            return null;

        var line = sseLine.TrimEnd('\r');

        // SSE comments (keep-alives) start with ':'; other field names are not ours to read.
        if (!line.StartsWith(DataPrefix, StringComparison.Ordinal))
            return null;

        var payload = line[DataPrefix.Length..].TrimStart();

        if (payload.Equals(DoneSentinel, StringComparison.Ordinal))
            return BuildDone();

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
            return ParseObject(document.RootElement);
    }

    /// <summary>
    /// Closes a stream that ended without a <c>[DONE]</c> sentinel, so a consumer always sees a
    /// terminal frame. Returns null when one was already emitted.
    /// </summary>
    public LlmStreamChunk? Finish() => _doneEmitted ? null : BuildDone();

    /// <summary>
    /// Parses a complete (non-streamed) response body into a single <see cref="LlmResponse"/>.
    /// The buffered shape nests under <c>choices[0].message</c> and carries whole values rather
    /// than fragments, so it needs none of the accumulator above.
    /// </summary>
    public static LlmResponse? ParseBuffered(JsonElement root, int contextWindow)
    {
        if (!root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
            return null;

        var choice = choices[0];
        if (!choice.TryGetProperty("message", out var message))
            return null;

        var content = message.TryGetProperty("content", out var contentElement) &&
                      contentElement.ValueKind == JsonValueKind.String
            ? contentElement.GetString()?.Trim()
            : null;

        var toolCalls = new List<LlmToolCall>();
        if (message.TryGetProperty("tool_calls", out var toolCallsElement) &&
            toolCallsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in toolCallsElement.EnumerateArray())
            {
                if (!element.TryGetProperty("function", out var function) ||
                    !function.TryGetProperty("name", out var nameElement))
                    continue;

                var name = nameElement.GetString();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var arguments = new Dictionary<string, string?>();
                if (function.TryGetProperty("arguments", out var argsElement))
                    JsonArgumentReader.Read(argsElement, arguments);

                toolCalls.Add(new LlmToolCall(new Tool(name), arguments));
            }
        }

        return new LlmResponse(content, toolCalls, ParseUsage(root, contextWindow));
    }

    /// <summary>
    /// Reads token accounting from a response or a terminal stream frame. The OpenAI field names
    /// differ from Ollama's, and the context window is stamped from configuration because the
    /// response does not echo it. Absent on every frame except the last, and absent entirely
    /// unless the request asked for <c>stream_options.include_usage</c>.
    /// </summary>
    public static LlmUsage? ParseUsage(JsonElement root, int contextWindow)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;

        var hasPrompt = usage.TryGetProperty("prompt_tokens", out var promptElement) &&
                        promptElement.ValueKind == JsonValueKind.Number;
        var hasCompletion = usage.TryGetProperty("completion_tokens", out var completionElement) &&
                            completionElement.ValueKind == JsonValueKind.Number;

        if (!hasPrompt && !hasCompletion)
            return null;

        return new LlmUsage(
            hasPrompt ? promptElement.GetInt32() : 0,
            hasCompletion ? completionElement.GetInt32() : 0,
            contextWindow);
    }

    private LlmStreamChunk? ParseObject(JsonElement root)
    {
        // Usage rides a trailing frame that usually carries no choices at all. Hold it for the
        // terminal frame rather than emitting it on its own.
        if (ParseUsage(root, _contextWindow) is { } usage)
            _usage = usage;

        if (!root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
            return null;

        var choice = choices[0];
        string? contentDelta = null;
        string? thinkingDelta = null;

        if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
        {
            contentDelta = ReadNonEmptyString(delta, "content");

            // llama-server splits reasoning out of the reply when the template produces it. The
            // field is absent on a model or template that does not reason, which is why thinking
            // being configured on is never on its own evidence that any arrived.
            thinkingDelta = ReadNonEmptyString(delta, "reasoning_content");

            AccumulateToolCalls(delta);
        }

        var finished = choice.TryGetProperty("finish_reason", out var finishElement) &&
                       finishElement.ValueKind == JsonValueKind.String;

        // A finished choice is where the assembled tool calls become complete and safe to emit.
        // They ride a non-final frame, exactly as Ollama delivers them, so the agent loop sees one
        // shape from both backends.
        List<LlmToolCall>? toolCalls = null;
        if (finished && !_toolCallsEmitted && _partials.Count > 0)
        {
            toolCalls = DrainToolCalls();
            _toolCallsEmitted = true;
        }

        if (contentDelta is null && thinkingDelta is null && toolCalls is null)
            return null;

        return new LlmStreamChunk(contentDelta, toolCalls, Done: false, Usage: null, ThinkingDelta: thinkingDelta);
    }

    /// <summary>
    /// Folds one frame's <c>delta.tool_calls</c> partials into the accumulator. Each entry is
    /// keyed by <c>index</c>; the name arrives once and the arguments arrive as string fragments
    /// that must be concatenated in order. A server that sends a whole tool call in one frame
    /// lands here too — that is simply a single fragment.
    /// </summary>
    private void AccumulateToolCalls(JsonElement delta)
    {
        if (!delta.TryGetProperty("tool_calls", out var toolCalls) ||
            toolCalls.ValueKind != JsonValueKind.Array)
            return;

        var fallbackIndex = _partials.Count;

        foreach (var element in toolCalls.EnumerateArray())
        {
            var index = element.TryGetProperty("index", out var indexElement) &&
                        indexElement.ValueKind == JsonValueKind.Number
                ? indexElement.GetInt32()
                : fallbackIndex++;

            if (!_partials.TryGetValue(index, out var partial))
                _partials[index] = partial = new PartialToolCall();

            if (!element.TryGetProperty("function", out var function) ||
                function.ValueKind != JsonValueKind.Object)
                continue;

            if (ReadNonEmptyString(function, "name") is { } name)
                partial.Name ??= name;

            if (function.TryGetProperty("arguments", out var argsElement) &&
                argsElement.ValueKind == JsonValueKind.String)
                partial.Arguments.Append(argsElement.GetString());
        }
    }

    private List<LlmToolCall> DrainToolCalls()
    {
        var assembled = new List<LlmToolCall>(_partials.Count);

        foreach (var partial in _partials.Values)
        {
            if (string.IsNullOrWhiteSpace(partial.Name))
                continue;

            var arguments = new Dictionary<string, string?>();
            JsonArgumentReader.ReadObjectString(partial.Arguments.ToString(), arguments);
            assembled.Add(new LlmToolCall(new Tool(partial.Name), arguments));
        }

        _partials.Clear();
        return assembled;
    }

    /// <summary>
    /// Builds the terminal frame, carrying the usage held back from the trailing frame and any
    /// tool calls the stream never marked finished.
    /// </summary>
    private LlmStreamChunk BuildDone()
    {
        List<LlmToolCall>? toolCalls = null;
        if (!_toolCallsEmitted && _partials.Count > 0)
        {
            toolCalls = DrainToolCalls();
            _toolCallsEmitted = true;
        }

        _doneEmitted = true;
        return new LlmStreamChunk(null, toolCalls, Done: true, Usage: _usage);
    }

    private static string? ReadNonEmptyString(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var element) ||
            element.ValueKind != JsonValueKind.String)
            return null;

        var value = element.GetString();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private sealed class PartialToolCall
    {
        public string? Name { get; set; }
        public StringBuilder Arguments { get; } = new();
    }
}
