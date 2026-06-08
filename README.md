# TheKrystalShip.Llm

A small, reusable C# library for talking to a **local LLM** (Ollama) and running a
**tool-calling agent loop** — the transport, conversation memory, and loop are generic;
your application supplies the tools, the system prompt, and the per-call policy.

It is deliberately application-agnostic: the library never learns what your tools *mean*.
Authorization, rate limits, and "what to offer this turn" are decided by the host and
handed to the loop per turn. This makes the same package usable from a Discord bot, an
ASP.NET chatbot, a CLI, etc.

## Install

```xml
<PackageReference Include="TheKrystalShip.Llm" Version="1.0.0" />
```

## Wire up (DI)

```csharp
using TheKrystalShip.Llm.Extensions;
using TheKrystalShip.Llm.Interfaces;

// Registers ILlmClient (Ollama), IConversationStore (in-memory), ILlmAgent.
// Binds options from config sections "Ollama", "Conversation", "LlmAgent".
services.AddLocalLlm(configuration);

// You MUST register your own tool dispatcher — the agent depends on it.
services.AddSingleton<IToolDispatcher, MyToolDispatcher>();
```

Example configuration:

```json
{
  "Ollama":       { "Endpoint": "http://localhost:11434", "Model": "gemma4:12b", "NumCtx": 32768, "TimeoutSeconds": 300, "Temperature": 0.3 },
  "Conversation": { "MaxMessages": 12, "IdleTimeoutMinutes": 15 },
  "LlmAgent":     { "MaxIterations": 8, "MaxToolOutputChars": 1500 }
}
```

## Run a turn

The host decides everything policy-related and passes it in an `AgentTurn`:

```csharp
// 1) Which tools to offer this turn (this set IS the whitelist).
var tools = canPerformActions ? AllTools : ReadOnlyTools;

// 2) Per-call gate — closure can hold state (e.g. an action counter for a cap).
int actions = 0;
Func<LlmToolCall, ToolGate> gate = call =>
{
    if (!IsMutating(call.Name)) return ToolGate.Allow;
    if (!canPerformActions)     return ToolGate.Refuse("You don't have permission to do that.");
    if (actions >= 5)           return ToolGate.Refuse("Action limit reached for this message.");
    actions++;
    return ToolGate.Allow;
};

var result = await agent.RunAsync(new AgentTurn
{
    ConversationId = $"{userId}:{channelId}",   // opaque; e.g. a web session id elsewhere
    UserPrompt     = prompt,
    SystemPrompt   = BuildSystemPrompt(...),     // built fresh by the host each turn
    Tools          = tools,
    Gate           = gate,
});

if (result.IsSuccess) Reply(result.Value!);
```

## What's in the box

| Area | Types |
|---|---|
| DTOs | `LlmMessage`/`LlmRole`, `LlmResponse`, `LlmToolCall`, `LlmToolDefinition`/`LlmToolParameter`, `Result`/`Result<T>` |
| Turn/policy | `AgentTurn`, `ToolGate` |
| Interfaces | `ILlmClient`, `IConversationStore`, `IToolDispatcher`, `ILlmAgent` |
| Ollama | `OllamaLlmClient`, `OllamaOptions` |
| Memory | `InMemoryConversationStore`, `ConversationOptions` |
| Loop | `LlmAgent`, `LlmAgentOptions` |
| DI | `AddLocalLlm(IConfiguration)` |

## Boundaries / responsibilities

- **Library owns:** the model↔tool round-trip, the iteration cap, tool-output truncation,
  the conversation persistence boundary (only user + final assistant text is stored).
- **Host owns:** the tool definitions, the `IToolDispatcher` that executes them, the system
  prompt, and the per-call `ToolGate` (authorization, caps, confirmation).

The `IToolDispatcher` is expected to enforce the tool whitelist (refuse unknown tools) and
never throw — return execution failures as result strings so the model can recover.

## Local development (consuming from a folder feed)

While iterating before this is published to a real feed, it's consumed from a local
folder feed (e.g. `/home/heisen/local-nuget`) via a `nuget.config` in the consumer:

```bash
dotnet pack TheKrystalShip.Llm/TheKrystalShip.Llm.csproj -c Release -o /home/heisen/local-nuget
```

> ⚠️ **Cache footgun:** NuGet caches the *extracted* package by version. Re-packing the
> **same** version (`1.0.0`) will be silently ignored by consumers — they keep using the
> cached copy, so your edits appear to have no effect. Either **bump `<Version>`** on each
> change, or clear the cached extraction after re-packing:
> ```bash
> rm -rf ~/.nuget/packages/thekrystalship.llm/<version>
> ```

## License

GPL-3.0-only.
