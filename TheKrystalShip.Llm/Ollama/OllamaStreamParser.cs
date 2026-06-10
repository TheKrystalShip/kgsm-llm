using System.Text.Json;

using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Llm.Ollama;

/// <summary>
/// Pure, HTTP-free parsing of Ollama <c>/api/chat</c> response frames. A non-streaming response
/// is a single JSON object; a streaming response (<c>stream:true</c>) is newline-delimited JSON —
/// one object per line. Keeping the parsing here lets the buffered and streaming paths share the
/// exact same tool-call extraction (so they can't drift) and makes frame parsing unit-testable
/// without a live backend.
/// </summary>
public static class OllamaStreamParser
{
    /// <summary>
    /// Parses one NDJSON streaming frame. Returns null for a blank line or malformed JSON — the
    /// reader simply skips it. Content deltas are returned verbatim (NOT trimmed: trimming each
    /// delta would swallow the spaces between tokens); the caller trims the assembled whole.
    /// </summary>
    public static LlmStreamChunk? ParseFrame(string jsonLine)
    {
        if (string.IsNullOrWhiteSpace(jsonLine))
            return null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(jsonLine);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            var done = root.TryGetProperty("done", out var doneElement) &&
                       doneElement.ValueKind == JsonValueKind.True;

            string? contentDelta = null;
            List<LlmToolCall>? toolCalls = null;

            if (root.TryGetProperty("message", out var message))
            {
                if (message.TryGetProperty("content", out var contentElement) &&
                    contentElement.ValueKind == JsonValueKind.String)
                {
                    var raw = contentElement.GetString();
                    if (!string.IsNullOrEmpty(raw))
                        contentDelta = raw;
                }

                var parsed = ParseToolCalls(message);
                if (parsed.Count > 0)
                    toolCalls = parsed;
            }

            return new LlmStreamChunk(contentDelta, toolCalls, done);
        }
    }

    /// <summary>
    /// Extracts the tool calls from a Ollama <c>message</c> element. Shared by the streaming and
    /// buffered clients so a single arg-coercion path serves both.
    /// </summary>
    public static List<LlmToolCall> ParseToolCalls(JsonElement messageElement)
    {
        var toolCalls = new List<LlmToolCall>();

        if (!messageElement.TryGetProperty("tool_calls", out var toolCallsElement) ||
            toolCallsElement.ValueKind != JsonValueKind.Array)
            return toolCalls;

        foreach (var toolCall in toolCallsElement.EnumerateArray())
        {
            if (!toolCall.TryGetProperty("function", out var function) ||
                !function.TryGetProperty("name", out var nameElement))
                continue;

            var name = nameElement.GetString();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var arguments = new Dictionary<string, string?>();
            if (function.TryGetProperty("arguments", out var argsElement))
                ExtractArguments(argsElement, arguments);

            toolCalls.Add(new LlmToolCall(name, arguments));
        }

        return toolCalls;
    }

    /// <summary>
    /// Arguments usually arrive as a JSON object, but some models emit a JSON string that itself
    /// contains an object. Handle both, coercing each value to a string. Malformed stringified
    /// arguments are skipped (best-effort, as before).
    /// </summary>
    private static void ExtractArguments(JsonElement argsElement, Dictionary<string, string?> into)
    {
        if (argsElement.ValueKind == JsonValueKind.String)
        {
            var raw = argsElement.GetString();
            if (string.IsNullOrWhiteSpace(raw))
                return;
            try
            {
                using var parsed = JsonDocument.Parse(raw);
                if (parsed.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var prop in parsed.RootElement.EnumerateObject())
                        into[prop.Name] = JsonValueToString(prop.Value);
            }
            catch (JsonException)
            {
                // Best-effort: a model that emits non-JSON stringified arguments just yields no args.
            }
            return;
        }

        if (argsElement.ValueKind == JsonValueKind.Object)
            foreach (var prop in argsElement.EnumerateObject())
                into[prop.Name] = JsonValueToString(prop.Value);
    }

    private static string? JsonValueToString(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Null => null,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => element.GetRawText(),
        _ => element.GetRawText()
    };
}
