using System.Text.Json;

namespace TheKrystalShip.Llm.Backends;

/// <summary>
/// Coerces a tool call's arguments into the flat string dictionary
/// <see cref="Models.LlmToolCall"/> carries. Shared by every backend parser so the coercion
/// can't drift between them: a model that emits <c>{"port": 27015}</c> must reach a tool as
/// <c>"27015"</c> whichever server relayed it.
/// </summary>
internal static class JsonArgumentReader
{
    /// <summary>
    /// Reads an arguments payload that is either a JSON object or a JSON string containing one.
    /// Ollama emits the object form; the OpenAI wire format always uses the string form. Malformed
    /// stringified arguments are skipped (best-effort) rather than failing the whole tool call.
    /// </summary>
    public static void Read(JsonElement argsElement, Dictionary<string, string?> into)
    {
        if (argsElement.ValueKind == JsonValueKind.String)
        {
            ReadObjectString(argsElement.GetString(), into);
            return;
        }

        if (argsElement.ValueKind == JsonValueKind.Object)
            foreach (var prop in argsElement.EnumerateObject())
                into[prop.Name] = ValueToString(prop.Value);
    }

    /// <summary>
    /// Reads a raw string that should contain a JSON object. A blank or malformed string yields no
    /// arguments — which is also how a truncated streamed fragment sequence degrades.
    /// </summary>
    public static void ReadObjectString(string? raw, Dictionary<string, string?> into)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return;

        try
        {
            using var parsed = JsonDocument.Parse(raw);
            if (parsed.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var prop in parsed.RootElement.EnumerateObject())
                    into[prop.Name] = ValueToString(prop.Value);
        }
        catch (JsonException)
        {
            // Best-effort: a model that emits non-JSON stringified arguments just yields no args.
        }
    }

    public static string? ValueToString(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Null => null,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => element.GetRawText(),
        _ => element.GetRawText()
    };
}
