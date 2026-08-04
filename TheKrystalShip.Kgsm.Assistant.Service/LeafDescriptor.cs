using TheKrystalShip.KGSM.LeafConfig;

// What the Control Panel shows about this service, declared beside the configuration it describes.
// TheKrystalShip.KGSM.LeafConfig reads this out of the built assemblies and writes
// deploy/kgsm-llm.leaf.json; deploy.sh installs that into /var/lib/kgsm/leaves/assistant.json, where
// kgsm-api scans for it. The service itself never reads any of this.

[assembly: Leaf(
    id: "assistant",
    displayName: "Assistant",
    unit: "kgsm-assistant-service.service",
    role: "The KGSM assistant — answers questions about this host and performs authorized actions on it.")]

// The sections this leaf owns live a layer below the host that binds them. Named rather than
// discovered: kgsm-bot compiles against some of the same projects, and a section it did not ask for
// appearing in its descriptor would fail a build in a repo nobody touched.
[assembly: LeafSectionAssembly("TheKrystalShip.Kgsm.Assistant.Infrastructure")]
[assembly: LeafSectionAssembly("TheKrystalShip.Kgsm.Assistant")]
[assembly: LeafSectionAssembly("TheKrystalShip.Rag")]

[assembly: LeafGroup("general", "General", 1)]
[assembly: LeafGroup("model", "Model", 2)]
[assembly: LeafGroup("agent", "Agent loop", 3)]
[assembly: LeafGroup("conversation", "Conversation history", 4)]
[assembly: LeafGroup("kgsm", "KGSM & leaf connections", 5)]
[assembly: LeafGroup("cache", "Inventory cache", 6)]
[assembly: LeafGroup("actions", "Actions & secrets", 7)]
[assembly: LeafGroup("discord", "Discord sign-in", 8)]
[assembly: LeafGroup("session", "Sessions", 9)]
[assembly: LeafGroup("websearch", "Web search", 10)]
[assembly: LeafGroup("webfetch", "Web fetch", 11)]
[assembly: LeafGroup("authoring", "Blueprint authoring", 12)]
[assembly: LeafGroup("rag", "Knowledge base", 13)]

// Lowest precedence first — the same order Program.cs registers them in.
[assembly: LeafFloorSource("appsettings", "/opt/kgsm-assistant/service/kgsm-assistant.settings.json")]
[assembly: LeafFloorSource("systemd-unit", "/etc/kgsm-assistant/systemd/kgsm-assistant-service.service")]
[assembly: LeafFloorSource("env-file", "/etc/kgsm-assistant/service.env")]

[assembly: LeafFrameworkNamespace("Logging__",
    "per-category filtering is open-ended: any category name is a valid key")]

[assembly: LeafFrameworkField("logLevel", "Logging__LogLevel__Default", "Log level",
    Description = "Minimum severity this leaf logs.",
    Group = "general",
    Type = LeafType.Enum,
    Values = ["Trace", "Debug", "Information", "Warning", "Error", "Critical"])]

// The host builder's own variable, read before any of this service's types exist.
[assembly: LeafFrameworkField("bindAddress", "ASPNETCORE_URLS", "Listen address",
    Description = "Address the assistant serves on. It stays on loopback; anything reaching it from outside this host comes through the Control Panel API.",
    Group = "general", Risk = LeafRisk.Wiring, PairedApiKey = "Api__AssistantBaseUrl",
    SettingsKey = "Urls")]

// ── TheKrystalShip.Llm's sections, described for this surface ────────────────
//
// Ollama, LlmAgent and Conversation are bound from types that library owns, and kgsm-bot binds the
// same ones. The two describe them differently and correctly — what the assistant says when it runs
// out of tool steps is not what the bot says — so the prose lives with the surface that shows it
// rather than on the shared type.

[assembly: LeafFrameworkField("promptsDirectory", "Prompts__Directory", "Prompt overrides directory",
    Description = "Directory of editable prompt files that replace the assistant's built-in system prompt and tool descriptions. Blank uses the built-in text. A file here outranks every other way of setting the same prompt.",
    Group = "general", Type = LeafType.Path, Risk = LeafRisk.Wiring, NoDefault = true)]

[assembly: LeafFrameworkField("ollamaEndpoint", "Ollama__Endpoint", "Ollama endpoint",
    Description = "Where the language model is served from. Everything the assistant says comes through here, so an unreachable endpoint leaves it unable to answer at all.",
    Group = "model", Risk = LeafRisk.Wiring)]

[assembly: LeafFrameworkField("ollamaModel", "Ollama__Model", "Model",
    Description = "Name of the model to run, as Ollama knows it. It has to be pulled on the endpoint above and has to support tool calling, or the assistant can answer but never act.",
    Group = "model", Risk = LeafRisk.Wiring)]

[assembly: LeafFrameworkField("ollamaNumCtx", "Ollama__NumCtx", "Context window",
    Description = "How many tokens of context the model is given. Larger holds more conversation and more tool output at once, and costs more memory on the machine serving the model.",
    Group = "model", Type = LeafType.Int, Min = 512, Unit = "tokens")]

[assembly: LeafFrameworkField("ollamaTimeoutSec", "Ollama__TimeoutSeconds", "Model timeout",
    Description = "How long to wait for the model to finish a response before giving up on it.",
    Group = "model", Type = LeafType.Int, Min = 1, Unit = "s")]

[assembly: LeafFrameworkField("ollamaTemperature", "Ollama__Temperature", "Temperature",
    Description = "How much the model varies its wording. Low keeps answers consistent and literal, which is what tool calling wants; high makes them more inventive.",
    Group = "model", Type = LeafType.Float, Min = 0, Max = 2)]

[assembly: LeafFrameworkField("ollamaSeed", "Ollama__Seed", "Sampling seed",
    Description = "Fixes the model's randomness so the same question gives the same answer, which is useful when comparing runs. Leave it unset for normal use.",
    Group = "model", Type = LeafType.Int)]

[assembly: LeafFrameworkField("ollamaThink", "Ollama__Think", "Thinking mode",
    Description = "Asks the model to reason before answering, on models that support it. Slower, and only worth it for a model trained for it.",
    Group = "model", Type = LeafType.Bool)]

[assembly: LeafFrameworkField("agentMaxIterations", "LlmAgent__MaxIterations", "Maximum tool steps",
    Description = "How many times the assistant may call a tool while working on one message before it has to answer with what it has.",
    Group = "agent", Type = LeafType.Int, Min = 1)]

[assembly: LeafFrameworkField("agentMaxToolOutputChars", "LlmAgent__MaxToolOutputChars", "Tool output limit",
    Description = "How much of a tool's output the model is shown. Truncating keeps a long listing from crowding out the conversation.",
    Group = "agent", Type = LeafType.Int, Min = 100, Unit = "chars")]

[assembly: LeafFrameworkField("agentIterationLimitReply", "LlmAgent__IterationLimitReply", "Step-limit reply",
    Description = "What the assistant says when it hits the tool-step limit without reaching an answer.",
    Group = "agent")]

[assembly: LeafFrameworkField("conversationDbPath", "Conversation__DatabasePath", "Conversation database",
    Description = "File holding past conversations, which is both the assistant's memory and the record the Control Panel reads. Pointing it elsewhere starts an empty history and leaves the old one behind.",
    Group = "conversation", Type = LeafType.Path, Risk = LeafRisk.Destructive)]
