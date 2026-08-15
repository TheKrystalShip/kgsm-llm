using System.Text.Json;

using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Llm.Backends.LlamaCpp;

/// <summary>
/// Builds the <c>/v1/chat/completions</c> request body. Two things differ from Ollama's native
/// shape and both are handled here:
/// <list type="bullet">
/// <item>a tool call's arguments travel as a JSON <b>string</b>, not an object;</item>
/// <item>a tool result is addressed by <c>tool_call_id</c>, not by tool name.</item>
/// </list>
/// <para>
/// <see cref="LlmMessage"/> carries no call id — it identifies a result by the tool it came from,
/// which is all Ollama needs. Ids are therefore assigned here, per request, by walking the history
/// in order: each assistant tool call takes the next id, and each tool result claims the oldest
/// outstanding call of the same name. The conversation is rebuilt on every request, so the same
/// history always produces the same ids and none of this has to be persisted.
/// </para>
/// </summary>
public static class LlamaCppRequestBuilder
{
    public static Dictionary<string, object?> Build(
        LlmBackendOptions options,
        LlamaCppOptions llamaCpp,
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmToolDefinition>? tools,
        bool stream,
        bool think)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = options.Model,
            ["stream"] = stream,
            ["temperature"] = options.Temperature,
            ["messages"] = BuildMessages(messages)
        };

        // The context window is fixed at launch (-c) and llama-server ignores a per-request value,
        // so it is not sent. It is still read from configuration to stamp token accounting.

        if (options.Seed is int seed)
            body["seed"] = seed;

        if (stream)
            // Without this the stream carries no token counts at all, and every turn would report
            // usage as unknown.
            body["stream_options"] = new Dictionary<string, object?> { ["include_usage"] = true };

        if (tools is { Count: > 0 })
        {
            body["tools"] = tools.Select(ToolSchema.BuildFunction).ToArray();
            body["parallel_tool_calls"] = llamaCpp.ParallelToolCalls;
        }

        // Reasoning is a property of the chat template, reached through the variable it declares.
        // A template that declares none ignores this, which is the same outcome as not sending it.
        if (think && !string.IsNullOrWhiteSpace(llamaCpp.ThinkingTemplateKwarg))
            body["chat_template_kwargs"] = new Dictionary<string, object?>
            {
                [llamaCpp.ThinkingTemplateKwarg] = true
            };

        return body;
    }

    private static object[] BuildMessages(IReadOnlyList<LlmMessage> messages)
    {
        var payloads = new List<object>(messages.Count);

        // Assistant tool calls awaiting their result, oldest first, as (tool name, assigned id).
        var outstanding = new List<(string Name, string Id)>();
        var nextId = 0;

        foreach (var message in messages)
        {
            switch (message.Role)
            {
                case LlmRole.Assistant when message.ToolCalls is { Count: > 0 }:
                {
                    var calls = new List<object>(message.ToolCalls.Count);
                    foreach (var call in message.ToolCalls)
                    {
                        var id = $"call_{nextId++}";
                        outstanding.Add((call.Name.Name, id));
                        calls.Add(new
                        {
                            id,
                            type = "function",
                            function = new
                            {
                                name = call.Name.Name,
                                arguments = JsonSerializer.Serialize(call.Arguments)
                            }
                        });
                    }

                    payloads.Add(new Dictionary<string, object?>
                    {
                        ["role"] = "assistant",
                        ["content"] = message.Content ?? string.Empty,
                        ["tool_calls"] = calls.ToArray()
                    });
                    break;
                }

                case LlmRole.Tool:
                {
                    var payload = new Dictionary<string, object?>
                    {
                        ["role"] = "tool",
                        ["content"] = message.Content ?? string.Empty
                    };

                    if (ClaimCallId(outstanding, message.ToolName?.Name) is { } id)
                        payload["tool_call_id"] = id;

                    payloads.Add(payload);
                    break;
                }

                default:
                    payloads.Add(new Dictionary<string, object?>
                    {
                        ["role"] = message.Role.ToString().ToLowerInvariant(),
                        ["content"] = message.Content ?? string.Empty
                    });
                    break;
            }
        }

        return [.. payloads];
    }

    /// <summary>
    /// Takes the oldest outstanding call of the given tool, or the oldest of any tool when the name
    /// matches nothing — a replayed history that was trimmed mid-round can hold a result whose call
    /// is no longer in the window, and dropping the id there would make the whole request invalid.
    /// Returns null only when nothing is outstanding at all.
    /// </summary>
    private static string? ClaimCallId(List<(string Name, string Id)> outstanding, string? toolName)
    {
        if (outstanding.Count == 0)
            return null;

        var index = toolName is null
            ? 0
            : outstanding.FindIndex(c => string.Equals(c.Name, toolName, StringComparison.Ordinal));

        if (index < 0)
            index = 0;

        var id = outstanding[index].Id;
        outstanding.RemoveAt(index);
        return id;
    }
}
