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
[assembly: LeafGroup("authorization", "Who may act", 11)]
[assembly: LeafGroup("session", "Sessions", 9)]
[assembly: LeafGroup("websearch", "Web search", 10)]
[assembly: LeafGroup("webfetch", "Web fetch", 11)]
[assembly: LeafGroup("speech", "Speaking answers", 12)]
[assembly: LeafGroup("authoring", "Blueprint authoring", 12)]
[assembly: LeafGroup("rag", "Knowledge base", 13)]
[assembly: LeafGroup("notifications", "Notifications", 14)]

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
// Llm, LlmAgent and Conversation are bound from types that library owns, and it is published for
// consumers outside this repo. The prose describing them belongs to the surface that shows it —
// what this assistant says when it runs out of tool steps is not what another surface would say —
// so it lives here rather than on the shared type.

[assembly: LeafFrameworkField("promptsDirectory", "Prompts__Directory", "Prompt overrides directory",
    Description = "Directory of editable prompt files that replace the assistant's built-in system prompt and tool descriptions. Blank uses the built-in text. A file here outranks every other way of setting the same prompt.",
    Group = "general", Type = LeafType.Path, Risk = LeafRisk.Wiring, NoDefault = true)]

// ── Speaking and listening ───────────────────────────────────────────────────
// The models are not this leaf's — kgsm-speech holds them, one engine per host serving every surface,
// and the voice is set there. What is settable here is only whether this assistant asks it for
// anything, and where to find it.

[assembly: LeafFrameworkField("speechEnabled", "Speech__Enabled", "Use the host's speech engine",
    Description = "Whether this assistant reads answers aloud and transcribes voice notes. An answer is synthesised as it is written — each sentence spoken while the next is still being composed, so the first plays before the answer is finished — and a recording sent from a browser is transcribed by the same engine, primed with this host's server names. One switch because one engine does both. Costs nothing on a turn that asked for neither, and nothing at all on a host with no speech engine installed.",
    Group = "speech")]

[assembly: LeafFrameworkField("speechSocket", "Speech__SocketPath", "Speech engine socket",
    Description = "The socket the host's speech engine (kgsm-speech) listens on. Blank uses the standard path. Without that leaf installed this assistant still answers — in text, to people who typed the question, as it does for every surface that asks for no audio.",
    Group = "speech", Type = LeafType.Path, Risk = LeafRisk.Wiring, NoDefault = true)]

[assembly: LeafFrameworkField("llmProvider", "Llm__Provider", "Inference server",
    Description = "Which local server runs the model: Ollama, which loads a model by name and manages it, or LlamaCpp, which talks to a llama-server already serving one. The choice reaches nothing above it — the same model, prompts and tools are used either way — but each expects its own endpoint, and llama-server has to be started with tool calling enabled or the assistant can answer and never act.",
    Group = "model", Risk = LeafRisk.Wiring, Type = LeafType.Enum, Values = ["Ollama", "LlamaCpp"])]

[assembly: LeafFrameworkField("llmEndpoint", "Llm__Endpoint", "Model endpoint",
    Description = "Where the language model is served from. Everything the assistant says comes through here, so an unreachable endpoint leaves it unable to answer at all.",
    Group = "model", Risk = LeafRisk.Wiring)]

[assembly: LeafFrameworkField("llmModel", "Llm__Model", "Model",
    Description = "Which model to talk to. Ollama resolves this as a pulled tag and loads it on demand; llama-server serves whatever it was launched with and only echoes the name back. Either way it has to support tool calling, or the assistant can answer but never act.",
    Group = "model", Risk = LeafRisk.Wiring)]

[assembly: LeafFrameworkField("llmContextWindow", "Llm__ContextWindow", "Context window",
    Description = "How many tokens of context the model is given. Larger holds more conversation and more tool output at once, and costs more memory on the machine serving the model. On llama-server this is fixed when the server starts, and the value here has to match what it was started with — it is what token counts are measured against.",
    Group = "model", Type = LeafType.Int, Min = 512, Unit = "tokens")]

[assembly: LeafFrameworkField("llmTimeoutSec", "Llm__TimeoutSeconds", "Model timeout",
    Description = "How long to wait for the model to finish a response before giving up on it.",
    Group = "model", Type = LeafType.Int, Min = 1, Unit = "s")]

[assembly: LeafFrameworkField("llmTemperature", "Llm__Temperature", "Temperature",
    Description = "How much the model varies its wording. Low keeps answers consistent and literal, which is what tool calling wants; high makes them more inventive.",
    Group = "model", Type = LeafType.Float, Min = 0, Max = 2)]

[assembly: LeafFrameworkField("llmSeed", "Llm__Seed", "Sampling seed",
    Description = "Fixes the model's randomness so the same question gives the same answer, which is useful when comparing runs. Leave it unset for normal use.",
    Group = "model", Type = LeafType.Int)]

[assembly: LeafFrameworkField("llmThink", "Llm__Think", "Thinking mode",
    Description = "Asks the model to reason before answering, on models that support it. Slower, and only worth it for a model trained for it.",
    Group = "model", Type = LeafType.Bool)]

[assembly: LeafFrameworkField("llamaCppApiKey", "Llm__LlamaCpp__ApiKey", "llama-server API key",
    Description = "Token to send when llama-server was started demanding one. Blank sends nothing, which is what a server listening only on this machine wants. Ignored unless the inference server is LlamaCpp.",
    Group = "model", Risk = LeafRisk.Wiring, NoDefault = true)]

[assembly: LeafFrameworkField("llamaCppThinkingKwarg", "Llm__LlamaCpp__ThinkingTemplateKwarg",
    "Thinking template variable",
    Description = "The name llama-server's chat template gives the variable that turns reasoning on. Templates spell it differently, and a template declaring no such variable ignores it — thinking is a property of the template the server was started with, not something a request can add. Ignored unless the inference server is LlamaCpp.",
    Group = "model", Risk = LeafRisk.Wiring)]

[assembly: LeafFrameworkField("llamaCppDryMultiplier", "Llm__LlamaCpp__DryMultiplier",
    "Repetition backstop",
    Description = "Stops the model getting stuck repeating itself. Without it a model that starts looping keeps going until it has filled its whole context, which takes minutes and ends with no answer at all. Zero turns it off. Ignored unless the inference server is LlamaCpp.",
    Group = "model", Type = LeafType.Float, Min = 0, Max = 10)]

[assembly: LeafFrameworkField("llamaCppDryBase", "Llm__LlamaCpp__DryBase",
    "Repetition backstop growth",
    Description = "How much harder the repetition backstop pushes back as a repeated passage gets longer. Ignored unless the inference server is LlamaCpp.",
    Group = "model", Type = LeafType.Float, Min = 1, Max = 8, Risk = LeafRisk.Wiring)]

[assembly: LeafFrameworkField("llamaCppDryAllowedLength", "Llm__LlamaCpp__DryAllowedLength",
    "Repetition allowance",
    Description = "How many words the model may repeat word-for-word before the backstop steps in. Too low and it interferes with things that repeat for good reason, like the keys in a config file. Ignored unless the inference server is LlamaCpp.",
    Group = "model", Type = LeafType.Int, Min = 1, Unit = "tokens", Risk = LeafRisk.Wiring)]

[assembly: LeafFrameworkField("llamaCppDryPenaltyLastN", "Llm__LlamaCpp__DryPenaltyLastN",
    "Repetition lookback",
    Description = "How far back the model looks for something it has already said. -1 checks everything it has written so far. Ignored unless the inference server is LlamaCpp.",
    Group = "model", Type = LeafType.Int, Min = -1, Unit = "tokens", Risk = LeafRisk.Wiring)]

[assembly: LeafFrameworkField("llamaCppParallelToolCalls", "Llm__LlamaCpp__ParallelToolCalls",
    "Allow parallel tool calls",
    Description = "Lets the model ask for several tools at once in a single step. Off matches how the assistant is prompted and measured — it works one step at a time — so turning this on changes behaviour that nothing else expects. Ignored unless the inference server is LlamaCpp.",
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

[assembly: LeafFrameworkField("agentEmptyReplyReply", "LlmAgent__EmptyReplyReply", "No-answer reply",
    Description = "What the assistant says when it finishes without writing an answer at all. Without something to say it would simply go quiet, which is indistinguishable from it ignoring you.",
    Group = "agent")]

[assembly: LeafFrameworkField("conversationDbPath", "Conversation__DatabasePath", "Conversation database",
    Description = "File holding past conversations, which is both the assistant's memory and the record the Control Panel reads. Pointing it elsewhere starts an empty history and leaves the old one behind.",
    Group = "conversation", Type = LeafType.Path, Risk = LeafRisk.Destructive)]

[assembly: LeafFrameworkField("conversationCompactAtPercent", "Conversation__CompactAtPercent",
    "Summarise a conversation at",
    Description = "How full the context window may get before the assistant folds a conversation into a summary on its own. Left to people to ask for, this never happens, and a conversation grows until the model quietly loses the start of it. Nothing is deleted — the full transcript is kept, and only what the model replays gets shorter. 0 switches it off.",
    Group = "conversation", Type = LeafType.Int, Min = 0, Max = 95, Unit = "%")]

// ── TheKrystalShip.KGSM.Auth's section, described for this surface ───────────
// The shared authorization block. The type lives in the auth package, which is deliberately free of
// every dependency including this one, so its keys are described here rather than on the type. Only
// the application is described: signing in establishes who someone is, and what they may do — ask
// questions, run actions, review other people's conversations — is on their KGSM account. The guild
// and role ids in that same shared file are kgsm-bot's and bind to nothing here. This surface's own
// callback URL and scopes are not among them either and stay on DiscordOAuth.

[assembly: LeafFrameworkField("authClientId", "KgsmAuth__Providers__discord__ClientId", "Discord application id",
    Description = "The Discord application users sign in through. The same application as the bot's.",
    Group = "authorization", Risk = LeafRisk.Wiring, NoDefault = true)]

[assembly: LeafFrameworkField("authClientSecret", "KgsmAuth__Providers__discord__ClientSecret", "Discord client secret",
    Description = "Secret for that application, used to complete a sign-in server-side.",
    Group = "authorization", Type = LeafType.Secret, Risk = LeafRisk.Wiring, NoDefault = true)]
