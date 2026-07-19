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

/// <summary>
/// Where to reach the kgsm-monitor's metrics socket. Bound from the "Monitor" config section. The
/// monitor serves its latest frame over an unauthenticated, pull-only <c>GET /metrics</c> on a
/// unix-domain socket; the metrics adapter scrapes it for the <c>get_performance</c> tool. Optional —
/// with no monitor reachable the adapter fails closed (reports the monitor unavailable), so this is a
/// path, not a dependency.
/// </summary>
public sealed class MonitorOptions
{
    public const string Section = "Monitor";

    /// <summary>Path to the monitor's metrics unix socket. A standard install serves it here.</summary>
    public string SocketPath { get; set; } = "/run/kgsm-monitor/metrics.sock";
}

/// <summary>
/// Connection to the kgsm-firewall authority — the host-firewall control daemon whose unix socket backs
/// the <c>get_network</c> read and the <c>open_ports</c> command. Optional — with no authority reachable
/// the adapter fails closed (reports the firewall unavailable / nothing changed), so this is a path, not
/// a dependency.
/// </summary>
public sealed class FirewallOptions
{
    public const string Section = "Firewall";

    /// <summary>Path to the kgsm-firewall control unix socket. A standard install serves it here.</summary>
    public string SocketPath { get; set; } = "/run/kgsm-firewall/firewall.sock";
}

/// <summary>Where the kgsm-watchdog control socket lives — backs the router/UPnP axis of get_network and
/// the opt-in router leg of open_ports (via kgsm-lib's IWatchdogClient).</summary>
public sealed class WatchdogOptions
{
    public const string Section = "Watchdog";

    /// <summary>Path to the kgsm-watchdog control unix socket. A standard install serves it here.</summary>
    public string SocketPath { get; set; } = "/run/kgsm-watchdog/control.sock";
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

/// <summary>
/// Local RAG retrieval settings. Bound from the "Rag" section — the SAME section the core's
/// <c>RagEmbeddingOptions</c> reads (each picks up its own keys); this is the retrieval/host half
/// (enable switch, where the index lives, how much to return), that is the embedder half. Retrieval
/// is off by default and fails closed (plan §D7): with <see cref="Enabled"/> false the host wires
/// no adapter, so <c>DisabledRetrieval</c> stays and the capability is simply omitted.
/// </summary>
public sealed class RagOptions
{
    public const string Section = "Rag";

    /// <summary>Master switch. False (default) → the host registers no retrieval adapter and the
    /// library's fail-closed <c>DisabledRetrieval</c> is what resolves. Flip to true once an index exists.</summary>
    public bool Enabled { get; set; }

    /// <summary>Path to the on-disk <c>.krag</c> index produced by the standalone indexer. A missing
    /// file is an expected state (indexer hasn't run yet) — retrieval fails closed until it appears.</summary>
    public string IndexPath { get; set; } = string.Empty;

    /// <summary>Chunks returned per query (the retrieval top-k). Small keeps the grounding text — and
    /// the context the model has to read — modest.</summary>
    public int TopK { get; set; } = 5;

    /// <summary>
    /// Cosine-similarity floor; hits below it are dropped. Default 0 — keep it permissive here. The
    /// "results too weak, fall back to web search" decision is the Phase 4 aggregator's job and reads
    /// the TOP score off what retrieval returns, so this stage must not pre-empt it by returning empty.
    /// </summary>
    public double MinScore { get; set; }

    // --- Index-time settings (the `kgsm-assistant index` verb / standalone indexer) ----------------
    // The retrieval path above ignores these; they configure how the index is BUILT, kept in the same
    // "Rag" block so an operator tunes one section (plan §4). The standalone indexer takes them as CLI
    // flags instead — it shares no config with the assistant, only the on-disk index (D6/D9).

    /// <summary>Docs to index — files and/or directories (walked recursively). Default = none (D2: operator-configured).</summary>
    public string[] Sources { get; set; } = [];

    /// <summary>Glob applied when walking a source directory. Default <c>*.md</c>.</summary>
    public string SourcePattern { get; set; } = "*.md";

    /// <summary>Chunk target size in characters. Changing it forces a full re-index (the carried-over chunks differ).</summary>
    public int ChunkSize { get; set; } = 2000;

    /// <summary>Chunk overlap in characters; must be &lt; <see cref="ChunkSize"/>.</summary>
    public int ChunkOverlap { get; set; } = 200;
}
