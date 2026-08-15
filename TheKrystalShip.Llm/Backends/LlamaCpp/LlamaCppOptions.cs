namespace TheKrystalShip.Llm.Backends.LlamaCpp;

/// <summary>
/// Knobs that exist only on llama.cpp's <c>llama-server</c>. Bound from the nested
/// <c>Llm:LlamaCpp</c> section; the model, endpoint, context window and sampling live on
/// <see cref="LlmBackendOptions"/> because they mean the same thing to every backend.
/// <para>
/// The server itself is configured by its launch flags, not from here — context size
/// (<c>-c</c>), KV cache type, batch sizes and the draft model are argv on the unit that starts
/// it. What remains here is what a single request can still choose.
/// </para>
/// </summary>
public class LlamaCppOptions
{
    public const string Section = "Llm:LlamaCpp";

    /// <summary>
    /// Bearer token, when the server was started with <c>--api-key</c>. Blank sends no
    /// Authorization header, which is what a loopback-bound server wants.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Whether the model may request several tool calls in one turn. llama-server disables this
    /// unless the request asks for it. Left off: the agent loop dispatches a round at a time, and
    /// one call per round is the behaviour every prompt and eval case was written against.
    /// </summary>
    public bool ParallelToolCalls { get; set; }

    /// <summary>
    /// The chat-template variable that turns reasoning on, passed through
    /// <c>chat_template_kwargs</c> when <see cref="LlmBackendOptions.Think"/> is set. Templates
    /// spell it differently (<c>enable_thinking</c> is the common one), and a template that
    /// declares no such variable ignores it — thinking is a property of the template the server
    /// was launched with, not something a request can add.
    /// </summary>
    public string ThinkingTemplateKwarg { get; set; } = "enable_thinking";
}
