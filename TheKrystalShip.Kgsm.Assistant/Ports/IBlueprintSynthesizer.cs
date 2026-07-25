namespace TheKrystalShip.Kgsm.Assistant.Ports;

/// <summary>
/// Reads the pages a blueprint research pass fetched and synthesizes the native-server fields with the
/// LLM — the capable-but-fallible counterpart to the deterministic regex extractor. It exists because a
/// regex cannot reliably pick the AUTHORITATIVE native launch method out of noisy sources (an official
/// doc vs a community Docker wrapper): reading the pages in context can. Returns <see langword="null"/>
/// to signal "couldn't synthesize — fall back to deterministic extraction", so the research aggregator
/// always has a working path even when the model is unconfigured, unreachable, or replies unparseably.
/// </summary>
public interface IBlueprintSynthesizer
{
    /// <summary>Synthesizes sourced native fields from the fetched pages, or <see langword="null"/> to
    /// fall back to the deterministic extractor. MUST NOT throw — a failure is a null result.</summary>
    Task<BlueprintResearchFindings?> SynthesizeAsync(
        string game, IReadOnlyList<(string Url, string Text)> pages, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IBlueprintSynthesizer"/> for hosts with no model wired for synthesis: always
/// returns <see langword="null"/> so research falls back to the deterministic regex extractor. Registered
/// with <c>TryAddSingleton</c>; a host with an LLM registers the real synthesizer afterward, and that
/// later registration is the one resolved.
/// </summary>
internal sealed class DisabledBlueprintSynthesizer : IBlueprintSynthesizer
{
    public Task<BlueprintResearchFindings?> SynthesizeAsync(
        string game, IReadOnlyList<(string Url, string Text)> pages, CancellationToken cancellationToken = default) =>
        Task.FromResult<BlueprintResearchFindings?>(null);
}
