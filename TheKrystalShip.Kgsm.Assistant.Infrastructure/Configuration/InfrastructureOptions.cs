namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Configuration;

/// <summary>
/// Where to find kgsm. Bound from the "KGSM" config section. Only the executable path is
/// needed — the adapters shell out to kgsm for reads/writes; events arrive out-of-band (the
/// HTTP service's webhook, or nothing in the CLI), so this graph binds NO event socket.
/// </summary>
public sealed class KgsmConnectionOptions
{
    public const string Section = "KGSM";

    public string Path { get; set; } = string.Empty;
}

/// <summary>TTLs for the in-process inventory cache. A backstop; hosts also invalidate explicitly
/// (the service from the kgsm webhook, the CLI after a confirmed mutation).</summary>
public sealed class InventoryCacheOptions
{
    public const string Section = "InventoryCache";

    public int InstancesTtlSeconds { get; set; } = 300;
    public int BlueprintsTtlSeconds { get; set; } = 600;
}

/// <summary>
/// Tavily web-search provider settings + the daily spend guard. Bound from the "WebSearch"
/// section. The tool is offered to everyone (read-only tier), so <see cref="MaxCallsPerDay"/>
/// is the wallet backstop in front of Tavily's free-credit limit; the per-message cap lives in
/// the assistant gate. Search is disabled (fails closed) whenever <see cref="ApiKey"/> is empty.
/// </summary>
public sealed class WebSearchOptions
{
    public const string Section = "WebSearch";

    /// <summary>
    /// Tavily API key (<c>tvly-…</c>). ENV-ONLY: leave empty in appsettings.json and supply
    /// <c>WebSearch__ApiKey</c> via the environment at runtime — same discipline as the
    /// DiscordOAuth secrets. Empty disables web search.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Results requested per search. Small keeps the grounding text (and context use) modest.</summary>
    public int MaxResults { get; set; } = 4;

    /// <summary><c>"basic"</c> (1 credit) or <c>"advanced"</c> (2 credits). Basic is plenty for lookups.</summary>
    public string SearchDepth { get; set; } = "basic";

    /// <summary>Per-request timeout. The agent loop blocks on this, so keep it short.</summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>Process-wide ceiling on searches per UTC day — the wallet backstop. Keep well under
    /// the provider's monthly free credit; given the read-only tier, this is the only spend gate.</summary>
    public int MaxCallsPerDay { get; set; } = 200;
}
