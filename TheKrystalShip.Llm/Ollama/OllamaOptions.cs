namespace TheKrystalShip.Llm.Ollama;

/// <summary>
/// Configuration options for the local Ollama LLM backend.
/// </summary>
public class OllamaOptions
{
    public const string Section = "Ollama";

    /// <summary>Base URL of the Ollama server, e.g. http://localhost:11434</summary>
    public string Endpoint { get; set; } = "http://localhost:11434";

    /// <summary>Model tag to use, e.g. gemma4:12b</summary>
    public string Model { get; set; } = "gemma4:12b";

    /// <summary>
    /// Context window size in tokens. This is a fixed KV-cache VRAM reservation;
    /// must stay within the GPU budget so inference never spills to system RAM.
    /// </summary>
    public int NumCtx { get; set; } = 32768;

    /// <summary>Request timeout in seconds (long generations can take a while).</summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Sampling temperature. Kept low so tool routing stays reliable/deterministic
    /// while replies remain natural.
    /// </summary>
    public double Temperature { get; set; } = 0.3;
}
