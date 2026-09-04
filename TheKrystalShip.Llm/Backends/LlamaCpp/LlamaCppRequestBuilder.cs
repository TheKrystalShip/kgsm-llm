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
/// <para>
/// A Gemma 4 template pairs a result with its call by <b>position</b> — llama.cpp folds the
/// assistant/tool run into one turn and matches the nth result to the nth call, reading the id only
/// to recover a missing name. So the order results are appended in is what actually carries the
/// pairing there, and the agent loop guarantees it by walking one index over both the calls and
/// their outputs. The ids remain correct for a server that does match on them.
/// </para>
/// </summary>
public static class LlamaCppRequestBuilder
{
    public static LlamaCppChatRequest Build(
        LlmBackendOptions options,
        LlamaCppOptions llamaCpp,
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmToolDefinition>? tools,
        bool stream,
        bool think)
    {
        bool hasTools = tools is { Count: > 0 };

        return new LlamaCppChatRequest
        {
            Model = options.Model,
            Stream = stream,
            Temperature = options.Temperature,
            Messages = BuildMessages(messages),

            // Only sent when explicitly configured (the eval harness's reproducible-run mode); an
            // absent seed leaves the backend's own unseeded sampling untouched.
            Seed = options.Seed,

            // Without this the stream carries no token counts at all, and every turn would report
            // usage as unknown.
            StreamOptions = stream ? new LlamaCppStreamOptions() : null,

            Tools = hasTools ? tools!.Select(ToolSchema.BuildFunction).ToList() : null,
            ParallelToolCalls = hasTools ? llamaCpp.ParallelToolCalls : null,

            // Only meaningful alongside a tool set; a server told to require a call from none of them
            // has nothing to do but fail.
            ToolChoice = hasTools && !string.IsNullOrWhiteSpace(llamaCpp.ToolChoice) ? llamaCpp.ToolChoice : null,

            // Reasoning is a property of the chat template, reached through the variable it declares.
            // A template that declares none ignores this, which is the same outcome as not sending it.
            //
            // The value is sent on EVERY request, including when reasoning is off. Omitting it does not
            // mean "off": llama-server's --reasoning defaults to `auto`, which detects that the template
            // supports reasoning and turns it on itself, so an absent variable reads as ENABLED. Measured
            // on gemma4:12b answering "what blueprints do we have installed": absent, every reply carried
            // a reasoning channel and cost 384 completion tokens for a 103-character answer; sent as
            // false, the channel is empty and the same answer costs 29. Tool calls are unaffected either
            // way (identical arguments, 152 tokens against 21).
            ChatTemplateKwargs = string.IsNullOrWhiteSpace(llamaCpp.ThinkingTemplateKwarg)
                ? null
                : new Dictionary<string, bool> { [llamaCpp.ThinkingTemplateKwarg] = think },

            // DRY penalises only VERBATIM repetition of a sequence already generated, which is what a
            // degenerate loop is made of; llama-server disables it by default and leaves no other
            // repetition control on either, so nothing bounds a loop but the context window. A run that
            // fills the window produces an empty reply after minutes of generation, which reaches a
            // person as silence.
            //
            // It is safe on the structured output tool arguments and file bodies are made of because the
            // default sequence breakers ('\n', ':', '"', '*') reset matching at every line and key —
            // measured on a 20-key .ini body, which came back byte-identical in shape with every key
            // present. This is why DRY is the backstop rather than repeat_penalty, which cannot tell a
            // loop from a config file's legitimately repeated punctuation.
            DryMultiplier = llamaCpp.DryMultiplier > 0 ? llamaCpp.DryMultiplier : null,
            DryBase = llamaCpp.DryMultiplier > 0 ? llamaCpp.DryBase : null,
            DryAllowedLength = llamaCpp.DryMultiplier > 0 ? llamaCpp.DryAllowedLength : null,
            DryPenaltyLastN = llamaCpp.DryMultiplier > 0 ? llamaCpp.DryPenaltyLastN : null,
        };
    }

    private static List<LlamaCppMessage> BuildMessages(IReadOnlyList<LlmMessage> messages)
    {
        var payloads = new List<LlamaCppMessage>(messages.Count);

        // Assistant tool calls awaiting their result, oldest first, as (tool name, assigned id).
        var outstanding = new List<(string Name, string Id)>();
        var nextId = 0;

        foreach (var message in messages)
        {
            switch (message.Role)
            {
                case LlmRole.Assistant when message.ToolCalls is { Count: > 0 }:
                {
                    var calls = new List<LlamaCppToolCall>(message.ToolCalls.Count);
                    foreach (var call in message.ToolCalls)
                    {
                        var id = $"call_{nextId++}";
                        outstanding.Add((call.Name.Name, id));
                        calls.Add(new LlamaCppToolCall
                        {
                            Id = id,
                            Function = new LlamaCppToolCallFunction
                            {
                                Name = call.Name.Name,
                                Arguments = SerializeArguments(call.Arguments),
                            },
                        });
                    }

                    payloads.Add(new LlamaCppMessage
                    {
                        Role = "assistant",
                        Content = message.Content ?? string.Empty,
                        ToolCalls = calls,
                    });
                    break;
                }

                case LlmRole.Tool:
                    payloads.Add(new LlamaCppMessage
                    {
                        Role = "tool",
                        Content = message.Content ?? string.Empty,
                        ToolCallId = ClaimCallId(outstanding, message.ToolName?.Name),
                    });
                    break;

                default:
                    payloads.Add(new LlamaCppMessage
                    {
                        Role = message.Role.ToString().ToLowerInvariant(),
                        Content = message.Content ?? string.Empty,
                    });
                    break;
            }
        }

        return payloads;
    }

    private static string SerializeArguments(IReadOnlyDictionary<string, string?> arguments) =>
        JsonSerializer.Serialize(
            new Dictionary<string, string?>(arguments, StringComparer.Ordinal),
            LlmWireJsonContext.Default.DictionaryStringString);

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
