using System.Net.Http.Headers;
using System.Net.Sockets;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Blueprints;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Configuration;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Fetch;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Kgsm;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Monitor;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Retrieval;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Search;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Extensions;
using TheKrystalShip.KGSM.Services;
using TheKrystalShip.Rag.Embedding;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Extensions;

/// <summary>
/// The single socket-safe seam that satisfies the assistant's kgsm-lib-backed ports
/// (<see cref="IServerInventory"/>, <see cref="IServerOperations"/>, <see cref="IWebSearch"/>)
/// for any host. Every host (the HTTP service, the CLI) calls this instead of hand-wiring the
/// kgsm-lib graph, so the deliberate "no socket listener" constraint below is captured ONCE.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the kgsm-lib command-executor graph, the port adapters that back the assistant,
    /// and the Tavily web-search adapter. Call AFTER <c>AddKgsmAssistant()</c> so the concrete
    /// <see cref="IWebSearch"/> here wins over the library's fail-closed <c>DisabledWebSearch</c>
    /// default (a silent axis — last registration wins).
    /// </summary>
    public static IServiceCollection AddKgsmAdapters(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<InventoryCacheOptions>(config.GetSection(InventoryCacheOptions.Section));
        services.Configure<WebSearchOptions>(config.GetSection(WebSearchOptions.Section));

        var kgsm = config.GetSection(KgsmConnectionOptions.Section).Get<KgsmConnectionOptions>()
            ?? throw new InvalidOperationException("KGSM configuration section is missing.");

        // We register the command-executor service graph (which shells out to kgsm) by hand
        // instead of KGSM.Lib's AddKgsmServices: that also wires IKgsmClient, whose constructor
        // calls Events.Initialize() and so starts reading the moment the graph is built. Whether
        // this process listens is a per-HOST decision, not a per-graph one — the CLI is a one-shot
        // with no cache to keep warm, the HTTP service is resident and wants events — so the
        // listener is a separate opt-in call (AddKgsmEventListener) and the shared graph stays
        // event-free. The journal settings ride on KgsmOptions for the hosts that do opt in.
        string engineJournalDir = string.IsNullOrWhiteSpace(kgsm.JournalDir)
            ? KgsmOptions.DefaultEventJournalDirectory
            : kgsm.JournalDir;

        services.AddSingleton(new KgsmOptions
        {
            KgsmPath = kgsm.Path,
            EventJournalDirectory = engineJournalDir,
            // Tail: this listener exists only to drop a cache when a blueprint changes. Replaying
            // history would re-invalidate for edits already reflected in what the next read returns.
            EventStartPosition = EventStartPosition.Tail
        });
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IKgsmCommandExecutor, KgsmCommandExecutor>();
        services.AddSingleton<ILogSubscriptionService, LogSubscriptionService>();
        services.AddSingleton<ILifecycleService, LifecycleService>();
        services.AddSingleton<IInstanceService, InstanceService>();
        services.AddSingleton<IInstanceFiles, InstanceFiles>(); // jailed file I/O for read/list/write_file — no socket dep, injects IInstanceService only
        services.AddSingleton<IBlueprintService, BlueprintService>();
        // A blueprint write is reported to the ecosystem by the engine's own `kgsm events emit`, so
        // the write-side authority needs the emit seam — shelling out like everything else here, no
        // socket. Provenance (who edited, through what) rides the emit, so the audit row downstream
        // names the real actor instead of the service account.
        services.AddSingleton<IEventManagementService, EventManagementService>();
        services.AddSingleton<IBlueprintFiles, BlueprintFiles>(); // create_blueprint's write-side authority — no socket dep
        // The two host-fact authorities behind host_info (and run_health_check's disk read):
        // ISystemService answers uptime/load/memory/disk, INetworkService answers the host's used
        // ports, port conflicts, and its addresses. Both shell out like the rest of this graph.
        services.AddSingleton<ISystemService, SystemService>();
        services.AddSingleton<INetworkService, NetworkService>();
        // Reading the event journal back, for the audit/timeline/root-cause tools. Unlike the listener
        // above this starts nothing and holds no position — it opens segments per query — so it belongs
        // in the shared graph rather than behind the per-host opt-in.
        //
        // EVERY producer's journal, not the engine's alone, and for this graph that is the difference
        // between answering an incident and not. The engine records what an operator asked for; the
        // supervisor records the crash, the give-up and who was playing, the firewall records the port
        // edges and the monitor records a threshold episode. trace_root_cause correlates exactly those
        // against a metrics window — reading one journal, it would search for the cause of a crash in a
        // record that does not contain the crash, and report honestly that it found nothing.
        //
        // It also registers the federated IEventSource, which AddKgsmEventListener then builds its
        // IEventService on. That call must stay AFTER this one; it no longer registers a source of its
        // own, because a later single-journal registration would win and quietly undo this.
        services.AddKgsmJournalFederation(
            cursorPath: null,
            startPosition: EventStartPosition.Tail,
            engineJournalDirectory: engineJournalDir);
        services.AddSingleton<IWatcherService, WatcherService>(); // port-reachability probe for run_health_check

        // One singleton inventory, exposed under both the read port and the invalidation seam so
        // a host's Invalidate() and the assistant's reads hit the SAME cache.
        services.AddSingleton<KgsmServerInventory>();
        services.AddSingleton<IServerInventory>(sp => sp.GetRequiredService<KgsmServerInventory>());
        services.AddSingleton<IInventoryInvalidation>(sp => sp.GetRequiredService<KgsmServerInventory>());

        // Ambient provenance (who/through-what) for the current action — set per request at /turn
        // and /confirm by the HTTP service, or once per process by the CLI; read at the kgsm
        // chokepoint so every mutation is attributable. Singleton: the AsyncLocal inside isolates
        // the value per call flow.
        services.AddSingleton<IInvocationContext, AsyncLocalInvocationContext>();
        services.AddSingleton<IServerOperations, KgsmServerOperations>();

        // --- Live per-server metrics (kgsm-monitor scrape) -------------------------------------
        // Backs the get_performance tool by scraping the monitor's GET /metrics over its unix socket.
        // Registered AFTER AddKgsmAssistant, so this concrete IServerMetrics wins over the library's
        // fail-closed UnavailableServerMetrics default. Additive: with no monitor reachable the scrape
        // maps every failure to "monitor unavailable" (the adapter never throws), so the assistant runs
        // standalone. A single socket-dialing handler is reused for every call (no per-call socket leak).
        var monitor = config.GetSection(MonitorOptions.Section).Get<MonitorOptions>() ?? new MonitorOptions();
        var monitorSocketPath = string.IsNullOrWhiteSpace(monitor.SocketPath)
            ? new MonitorOptions().SocketPath
            : monitor.SocketPath;
        services.AddHttpClient<IServerMetrics, KgsmServerMetrics>(client =>
        {
            // The request URI host is a placeholder the monitor ignores; the socket does the routing.
            client.BaseAddress = new Uri("http://localhost");
            // The monitor serves an in-memory frame and must be fast; bound it so a hung socket can
            // never stall a turn.
            client.Timeout = TimeSpan.FromSeconds(2);
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(monitorSocketPath), ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        });

        // --- Engine event history -------------------------------------------------------------
        // Backs events / trace_root_cause by reading the engine's event
        // journal through kgsm-lib — the record itself, so history needs no other service running and
        // works on a host with no leaves installed. Registered AFTER AddKgsmAssistant so this concrete
        // IEventHistory wins over the library's fail-closed UnavailableEventHistory default.
        services.AddSingleton<IEventHistory, KgsmEventHistory>();

        // --- Host firewall (kgsm-firewall authority) -------------------------------------------
        // Backs get_network (read) and open_ports (the confirmed mutation) by reaching the kgsm-firewall
        // authority through kgsm-lib's IFirewallService. Registered AFTER AddKgsmAssistant, so this
        // concrete INetworkInfo wins over the library's fail-closed UnavailableNetworkInfo default.
        // Additive: with no authority reachable the client throws FirewallException on connect, which the
        // adapter maps to "firewall unavailable" (never throws), so the assistant runs standalone. The
        // firewall authority is a separate daemon from the monitor, on its own socket.
        var firewall = config.GetSection(FirewallOptions.Section).Get<FirewallOptions>() ?? new FirewallOptions();
        var firewallSocketPath = string.IsNullOrWhiteSpace(firewall.SocketPath)
            ? new FirewallOptions().SocketPath
            : firewall.SocketPath;
        services.AddKgsmFirewallClient(firewallSocketPath);
        services.AddSingleton<INetworkInfo, KgsmNetworkInfo>();

        // --- Router / UPnP forwarding (kgsm-watchdog authority) --------------------------------
        // Backs the router axis of get_network and the opt-in router leg of open_ports by reaching the
        // watchdog through kgsm-lib's IWatchdogClient. Registered AFTER AddKgsmAssistant, so this concrete
        // IUpnpInfo wins over the library's fail-closed UnavailableUpnpInfo default. The watchdog CONTROL
        // client is HTTP-over-socket and starts no listener (unlike the event graph we deliberately omit
        // above), so it is safe to add here. Additive + fails closed: an unreachable daemon maps to
        // "watchdog unavailable" (the adapter never throws), so the assistant runs standalone.
        var watchdog = config.GetSection(WatchdogOptions.Section).Get<WatchdogOptions>() ?? new WatchdogOptions();
        var watchdogSocketPath = string.IsNullOrWhiteSpace(watchdog.SocketPath)
            ? new WatchdogOptions().SocketPath
            : watchdog.SocketPath;
        services.AddKgsmWatchdogClient(watchdogSocketPath);
        services.AddSingleton<IUpnpInfo, KgsmUpnpInfo>();

        // --- Server + host facts (the read half of the engine seam) ----------------------------
        // Registered here because the per-instance reads span both authorities: backups and versions
        // come from the engine, while player presence, the boot-autostart set and the console ring
        // come from the watchdog above. Both adapters win over the library's fail-closed defaults and
        // report an unreachable authority as unavailable, so a host with no supervisor still answers.
        services.AddSingleton<IServerFacts, KgsmServerFacts>();
        services.AddSingleton<IHostFacts, KgsmHostFacts>();

        // --- Web search (Tavily) ---------------------------------------------------------------
        // The assistant's web_search port. The API key is ENV-ONLY (WebSearch__ApiKey) and travels
        // as a default Bearer header; with no key the adapter fails closed. DailyCallBudget is the
        // singleton wallet cap (the tool is offered to everyone, so it's the only spend gate); the
        // per-message cap lives in the assistant.
        services.AddSingleton<DailyCallBudget>();
        services.AddHttpClient<IWebSearch, TavilyWebSearch>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<WebSearchOptions>>().Value;
            client.BaseAddress = new Uri("https://api.tavily.com/");
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds <= 0 ? 10 : options.TimeoutSeconds);
            if (!string.IsNullOrWhiteSpace(options.ApiKey))
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", options.ApiKey);
        });

        // --- URL fetch (fetch_url) --------------------------------------------------------------
        // The assistant's direct-fetch port: reads ONE specific page, unlike Tavily's summarized
        // search hits. Needs no API key — its own WebFetch:Enabled flag is the sole gate, independent
        // of WebSearchOptions.ApiKey. Auto-redirect is OFF (AllowAutoRedirect=false); HttpWebFetch
        // follows redirects manually so every hop re-enters SsrfGuard.ResolveSafeAsync via the
        // ConnectCallback below (the same unix-socket-style ConnectCallback pattern used for the
        // monitor/event-history clients above, here resolving+validating an arbitrary host instead of
        // a fixed socket path). Registered AFTER AddKgsmAssistant, so this concrete IWebFetch wins
        // over the library's fail-closed DisabledWebFetch default.
        services.Configure<WebFetchOptions>(config.GetSection(WebFetchOptions.Section));
        var webFetch = config.GetSection(WebFetchOptions.Section).Get<WebFetchOptions>() ?? new WebFetchOptions();
        services.AddSingleton<DailyFetchBudget>();
        services.AddHttpClient<IWebFetch, HttpWebFetch>((sp, client) =>
        {
            var o = sp.GetRequiredService<IOptions<WebFetchOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds <= 0 ? 8 : o.TimeoutSeconds);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("kgsm-assistant/1.0 (+fetch_url tool)");
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            // fetch_url follows redirects manually (below) so every hop is re-validated by the SSRF
            // guard — a blindly-followed redirect would otherwise be the obvious bypass.
            AllowAutoRedirect = false,
            ConnectCallback = (context, ct) => SsrfConnectCallback.ConnectAsync(context.DnsEndPoint, ct),
        });

        // The fetch_url TOOL is offered iff WebFetch:Enabled is true — computed here (the one place
        // that knows the adapter's config) into the assistant-facing FetchOptions.Available, mirroring
        // SearchOptions. PostConfigure alone is enough to make IOptions<FetchOptions> resolvable
        // even though nothing binds it from config directly (there is nothing to bind — Available is
        // purely computed).
        services.PostConfigure<FetchOptions>(o => o.Available = webFetch.Enabled);

        // --- Local RAG retrieval ---------------------------------------------------------------
        // Off by default and fails closed : only when Rag:Enabled is true do we wire the
        // concrete IRetrieval, which — because this runs AFTER AddKgsmAssistant — wins over the
        // library's DisabledRetrieval default. When disabled we register nothing, so the capability
        // is simply absent. The embedder + versioned index reader come from the Native-AOT
        // TheKrystalShip.Rag core; both RagOptions (host half) and RagEmbeddingOptions (embedder
        // half) bind the same "Rag" section, each reading its own keys. The index file is produced
        // out-of-band by the standalone indexer ; the provider only reads it.
        services.Configure<RagOptions>(config.GetSection(RagOptions.Section));
        var rag = config.GetSection(RagOptions.Section).Get<RagOptions>() ?? new RagOptions();
        if (rag.Enabled)
        {
            services.Configure<RagEmbeddingOptions>(config.GetSection(RagEmbeddingOptions.Section));
            // The embedding backend is chosen the same way the chat backend is: read once at
            // registration, because swapping it invalidates the index and is therefore a restart.
            var embedding = config.GetSection(RagEmbeddingOptions.Section).Get<RagEmbeddingOptions>()
                ?? new RagEmbeddingOptions();
            if (embedding.Provider == EmbeddingProvider.LlamaCpp)
                services.AddSingleton<IEmbeddingClient, LlamaCppEmbeddingClient>();
            else
                services.AddSingleton<IEmbeddingClient, OllamaEmbeddingClient>();
            services.AddSingleton<RagIndexProvider>();
            services.AddSingleton<IRetrieval, RagRetrieval>();
        }

        // --- Unified `search` tool -------------------------------------------
        // The aggregator's tunable thresholds (LocalMinScore/MaxContextChars) bind from the same
        // "Rag" section; the availability flags are COMPUTED here — the one place that knows BOTH
        // whether RAG is on and whether a web provider is configured — and decide whether the
        // `search` tool is offered at all. Both being off → the tool is omitted everywhere.
        var webSearch = config.GetSection(WebSearchOptions.Section).Get<WebSearchOptions>() ?? new WebSearchOptions();
        services.Configure<SearchOptions>(config.GetSection(SearchOptions.Section));
        services.PostConfigure<SearchOptions>(o =>
        {
            o.LocalEnabled = rag.Enabled;
            o.WebEnabled = !string.IsNullOrWhiteSpace(webSearch.ApiKey);
        });

        // --- Blueprint authoring (create_blueprint) ---------------------------------------------
        // The real pipeline: research (ISearch/IWebFetch above) → draft → persist/validate/test-install
        // via kgsm-lib's write-side authorities (IBlueprintFiles/IBlueprintService/IInstanceService,
        // already registered above) → verify (IServerOperations) → keep or stash. Registered AFTER
        // AddKgsmAssistant, so this concrete IBlueprintAuthoring wins over the library's fail-closed
        // DisabledBlueprintAuthoring default. Disabled (fails closed) by default — see
        // BlueprintAuthoringOptions's own remarks for why this is also what makes the tool safe to
        // force-OFFER in the eval harness without ever touching kgsm-lib there.
        services.Configure<BlueprintAuthoringOptions>(config.GetSection(BlueprintAuthoringOptions.Section));
        var blueprintAuthoring = config.GetSection(BlueprintAuthoringOptions.Section).Get<BlueprintAuthoringOptions>()
            ?? new BlueprintAuthoringOptions();
        if (!string.IsNullOrWhiteSpace(blueprintAuthoring.StashDir))
            services.AddSingleton<IBlueprintAttemptStore, FileSystemBlueprintAttemptStore>();
        services.AddSingleton<IBlueprintAuthoring, BlueprintAuthoringAggregator>();
        services.PostConfigure<BlueprintAuthoringFlags>(o => o.Available = blueprintAuthoring.Enabled);

        return services;
    }

    /// <summary>
    /// Opts a resident host into receiving kgsm's engine events, so a blueprint changed elsewhere
    /// (the Control Panel's library editor, another operator's CLI) drops this process's blueprint
    /// cache instead of being answered from a stale snapshot until the TTL expires.
    /// <para>
    /// Call AFTER <see cref="AddKgsmAdapters"/>, which carries the journal settings onto the
    /// kgsm-lib options. A no-op when <c>KGSM:JournalDir</c> is unset — the listener is opt-in per
    /// host because a one-shot CLI has no cache to keep warm, not because it would contend with
    /// anything: the journal is a file, so any number of readers coexist. The assistant runs fully
    /// without it; events only make an existing cache fresher, never gate a read.
    /// </para>
    /// </summary>
    public static IServiceCollection AddKgsmEventListener(this IServiceCollection services, IConfiguration config)
    {
        var kgsm = config.GetSection(KgsmConnectionOptions.Section).Get<KgsmConnectionOptions>();
        if (string.IsNullOrWhiteSpace(kgsm?.JournalDir))
            return services;

        // The source itself comes from AddKgsmAdapters' federation call, which is why this one must
        // run after it: what stays here is the dispatch on top of that source and the hosted service
        // that starts it. Registering a reader here as well would put a second tail on the same files
        // and, being last, would be the one every handler was wired to — the engine's journal alone.
        services.AddSingleton<IEventService, EventService>();
        services.AddHostedService<KgsmEventListener>();

        return services;
    }
}
