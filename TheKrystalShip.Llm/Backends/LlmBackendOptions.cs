namespace TheKrystalShip.Llm.Backends;

/// <summary>
/// Which local inference server serves the chat model. The value selects the
/// <see cref="Interfaces.ILlmClient"/> implementation registered by
/// <c>AddLocalLlm</c>; everything above that interface is identical either way.
/// </summary>
public enum LlmProvider
{
    /// <summary>Ollama's native <c>/api/chat</c> — NDJSON streaming, tool calls parsed by Ollama itself.</summary>
    Ollama,

    /// <summary>llama.cpp's <c>llama-server</c> — OpenAI-compatible <c>/v1/chat/completions</c>, SSE streaming.</summary>
    LlamaCpp
}

/// <summary>
/// Backend-independent configuration for the chat model. Every knob here means the same thing to
/// every provider, so a host describes the model once and picks the server separately.
/// <para>
/// Provider-specific knobs live on their own options type (<see cref="LlamaCpp.LlamaCppOptions"/>),
/// bound from a nested section. A setting belongs here only when both providers honour it.
/// </para>
/// </summary>
public class LlmBackendOptions
{
    public const string Section = "Llm";

    /// <summary>Which inference server to talk to.</summary>
    public LlmProvider Provider { get; set; } = LlmProvider.Ollama;

    /// <summary>Base URL of the inference server, e.g. http://localhost:11434 (Ollama) or http://localhost:8081 (llama-server).</summary>
    public string Endpoint { get; set; } = "http://localhost:11434";

    /// <summary>
    /// Model identifier. Ollama resolves it as a pulled tag (<c>gemma4:12b</c>) and loads it on
    /// demand; llama-server takes whatever name it was launched serving, and the field is only
    /// echoed back in responses.
    /// </summary>
    public string Model { get; set; } = "gemma4:12b";

    /// <summary>
    /// Context window in tokens. This is a fixed KV-cache VRAM reservation; it must stay within the
    /// GPU budget so inference never spills to system RAM.
    /// <para>
    /// Ollama accepts it per request (<c>num_ctx</c>). llama-server fixes it at launch (<c>-c</c>)
    /// and ignores anything sent per request, so there the value here must match the flag the
    /// server was started with — it is still read, because token accounting is reported against it.
    /// </para>
    /// </summary>
    public int ContextWindow { get; set; } = 32768;

    /// <summary>Request timeout in seconds (long generations can take a while).</summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Sampling temperature. Kept low so tool routing stays reliable/deterministic
    /// while replies remain natural.
    /// </summary>
    public double Temperature { get; set; } = 0.3;

    /// <summary>
    /// Optional RNG seed for sampling. When set, a backend produces reproducible output for an
    /// identical request (same model/options/prompt) — used by the eval harness to isolate a
    /// prompt change's effect from sampling noise. Null (the default) leaves sampling unseeded,
    /// which is the right choice for normal use.
    /// </summary>
    public int? Seed { get; set; }

    /// <summary>
    /// When true, enables the model's thinking/reasoning mode: it generates an internal
    /// chain-of-thought before producing its final answer. Thinking content streams as
    /// <see cref="Models.LlmStreamChunk.ThinkingDelta"/> and is never persisted to conversation
    /// history. Off by default — thinking adds latency and token cost.
    /// </summary>
    public bool Think { get; set; }
}
