namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Configuration;

/// <summary>
/// Where to find kgsm. Bound from the "KGSM" config section. <see cref="Path"/> is what the
/// adapters shell out to for every read and write; <see cref="JournalDir"/> is the separate,
/// optional inbound channel for engine events.
/// </summary>
public sealed class KgsmConnectionOptions
{
    public const string Section = "KGSM";

    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// The directory holding kgsm's append-only event journal, which this process tails to learn
    /// when a blueprint changed. Read-only and shared: the engine is the sole writer, any number of
    /// consumers read the same files with no coordination, and nothing here is claimed by this
    /// process.
    /// <para>
    /// Empty (the default) means this host reads no events, which is what the CLI wants — a
    /// one-shot invocation has no cache to keep warm, so tailing anything would be pure cost.
    /// That is now the whole reason the listener is opt-in: nothing prevents a second reader, so a
    /// CLI run alongside the resident service would be harmless, merely pointless.
    /// </para>
    /// </summary>
    public string JournalDir { get; set; } = string.Empty;
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
/// Direct-HTTP URL-fetch provider settings — reads ONE specific page (a doc, a Steam page, a raw
/// Dockerfile) that <c>IWebSearch</c> cannot (it only returns provider-summarized hits). Bound from
/// the "WebFetch" section. Needs no API key (it's a direct GET), so it is enabled INDEPENDENTLY of
/// <see cref="WebSearchOptions"/> — its own <see cref="Enabled"/> flag is the sole gate. Fails closed
/// (disabled) by default. The URL is model/user-influenced and this host is internet-exposed, so the
/// SSRF guard and the manual per-redirect-hop re-validation in the adapter (<c>HttpWebFetch</c>) are
/// load-bearing safety, not just config plumbing.
/// </summary>
public sealed class WebFetchOptions
{
    public const string Section = "WebFetch";

    /// <summary>Master switch. False (default) → the host registers no adapter and the library's
    /// fail-closed <c>DisabledWebFetch</c> resolves, so <c>fetch_url</c> is not offered.</summary>
    public bool Enabled { get; set; }

    /// <summary>Per-request timeout (connect + read). The agent loop blocks on this, so keep it short.</summary>
    public int TimeoutSeconds { get; set; } = 8;

    /// <summary>Hard cap on bytes read from the response body. Fetching stops and <c>Truncated</c> is
    /// set once this is hit, rather than buffering an unbounded page.</summary>
    public int MaxContentBytes { get; set; } = 3 * 1024 * 1024;

    /// <summary>Redirect hops followed before giving up. Auto-redirect is OFF on the underlying
    /// handler; the adapter follows manually and re-validates the SSRF guard on every hop — no hop is
    /// ever trusted blindly.</summary>
    public int MaxRedirects { get; set; } = 5;

    /// <summary>Process-wide ceiling on fetches per UTC day — the wallet backstop, mirroring
    /// <see cref="WebSearchOptions.MaxCallsPerDay"/>. The per-message cap lives in the assistant gate.</summary>
    public int MaxCallsPerDay { get; set; } = 200;

    /// <summary>Optional operator allowlist: each entry matches that exact host or any of its
    /// subdomains (e.g. "github.com" also matches "raw.githubusercontent.com" only if listed
    /// separately — subdomain matching is per-entry, not implicit across unrelated domains). Empty
    /// means no allowlist restriction beyond the built-in SSRF guard.</summary>
    public string[] AllowedHosts { get; set; } = [];

    /// <summary>Optional operator denylist, checked before the allowlist. Empty means none configured.</summary>
    public string[] DeniedHosts { get; set; } = [];
}

/// <summary>
/// The <c>create_blueprint</c> authoring pipeline's settings. Bound from the "BlueprintAuthoring"
/// section. Disabled (fails closed) by default like <see cref="WebFetchOptions"/> — flipping
/// <see cref="Enabled"/> is the sole gate the real <c>BlueprintAuthoringAggregator</c> checks before
/// touching kgsm-lib at all, so leaving it false keeps the pipeline inert everywhere it isn't explicitly
/// turned on (including inside the eval, which force-offers the tool for routing checks without ever
/// flipping this flag — see <c>Harness.cs</c>).
/// </summary>
public sealed class BlueprintAuthoringOptions
{
    public const string Section = "BlueprintAuthoring";

    /// <summary>Master switch. False (default) → <c>create_blueprint</c> honestly reports itself as not
    /// configured and never calls kgsm-lib's write-side blueprint/instance authorities.</summary>
    public bool Enabled { get; set; }

    /// <summary>Directory the admin "attempted" stash writes into (draft YAML + provenance + verify log
    /// per failed/infeasible attempt). Empty (default) → records are dropped rather than written
    /// (mirrors <see cref="RagOptions.IndexPath"/>'s "not configured yet" handling).</summary>
    public string StashDir { get; set; } = string.Empty;

    /// <summary>Bound on the persist→install→verify→repair loop — the first attempt runs the researched
    /// draft, each subsequent attempt runs a draft the repair step corrected from the real install tree +
    /// boot log. Kept small so a genuinely unrepairable source fails fast rather than flapping (3 allows the
    /// initial draft plus two evidence-driven repairs).</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>How long to poll the test-install for "booted + listening" before giving up on that
    /// attempt. Generous because a GB-scale server can take minutes to cold-boot; a crash exits the poll
    /// early (a server that came up then died isn't waited out), so the ceiling only applies to a genuinely
    /// slow boot.</summary>
    public int VerifyTimeoutSeconds { get; set; } = 240;

    /// <summary>Interval between verify polls.</summary>
    public int VerifyPollIntervalSeconds { get; set; } = 5;
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
