using System.Net.Http.Headers;
using System.Net.Sockets;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Configuration;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Kgsm;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Monitor;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Retrieval;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Search;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Extensions;
using TheKrystalShip.KGSM.Services;
using TheKrystalShip.Rag.Ollama;

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
        // instead of KGSM.Lib's AddKgsmServices: that also wires IUnixSocketClient / IEventService
        // / IKgsmClient, whose construction auto-starts the Unix-socket event listener and would
        // contend with the bot for the single kgsm event socket. The HTTP service receives events
        // over its /events webhook and the CLI doesn't follow events at all, so neither host may
        // bind that socket — hence we register only the IKgsmCommandExecutor graph the inventory
        // needs and omit those three socket-bound singletons. (SocketPath is left empty: nothing
        // here follows logs over the socket.)
        services.AddSingleton(new KgsmOptions { KgsmPath = kgsm.Path });
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IKgsmCommandExecutor, KgsmCommandExecutor>();
        services.AddSingleton<ILogSubscriptionService, LogSubscriptionService>();
        services.AddSingleton<ILifecycleService, LifecycleService>();
        services.AddSingleton<IInstanceService, InstanceService>();
        services.AddSingleton<IBlueprintService, BlueprintService>();
        services.AddSingleton<ISystemService, SystemService>();   // host disk for run_health_check
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

        // --- Engine event history (kgsm-monitor GET /events scrape) ----------------------------
        // Backs get_audit_log / get_change_timeline over the SAME monitor unix socket as the metrics
        // client above (Phase B/D of the event-history plan) — the assistant reads the monitor
        // directly, never via kgsm-api (plan §9, leaf independence). Registered AFTER AddKgsmAssistant,
        // so this concrete IEventHistory wins over the library's fail-closed UnavailableEventHistory
        // default. Additive: with no monitor reachable (or event history disabled on that host) every
        // read maps to "monitor unavailable" (the adapter never throws), so the assistant runs standalone.
        services.AddHttpClient<IEventHistory, KgsmEventHistory>(client =>
        {
            client.BaseAddress = new Uri("http://localhost");
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

        // --- Local RAG retrieval ---------------------------------------------------------------
        // Off by default and fails closed (plan §D7): only when Rag:Enabled is true do we wire the
        // concrete IRetrieval, which — because this runs AFTER AddKgsmAssistant — wins over the
        // library's DisabledRetrieval default. When disabled we register nothing, so the capability
        // is simply absent. The embedder + versioned index reader come from the Native-AOT
        // TheKrystalShip.Rag core; both RagOptions (host half) and RagEmbeddingOptions (embedder
        // half) bind the same "Rag" section, each reading its own keys. The index file is produced
        // out-of-band by the standalone indexer (plan §D6); the provider only reads it.
        services.Configure<RagOptions>(config.GetSection(RagOptions.Section));
        var rag = config.GetSection(RagOptions.Section).Get<RagOptions>() ?? new RagOptions();
        if (rag.Enabled)
        {
            services.Configure<RagEmbeddingOptions>(config.GetSection(RagEmbeddingOptions.Section));
            services.AddSingleton<IEmbeddingClient, OllamaEmbeddingClient>();
            services.AddSingleton<RagIndexProvider>();
            services.AddSingleton<IRetrieval, RagRetrieval>();
        }

        // --- Unified `search` tool (§3.4 aggregator) -------------------------------------------
        // The aggregator's tunable thresholds (LocalMinScore/MaxContextChars) bind from the same
        // "Rag" section; the availability flags are COMPUTED here — the one place that knows BOTH
        // whether RAG is on and whether a web provider is configured — and decide whether the
        // `search` tool is offered at all (§D7). Both being off → the tool is omitted everywhere.
        var webSearch = config.GetSection(WebSearchOptions.Section).Get<WebSearchOptions>() ?? new WebSearchOptions();
        services.Configure<SearchOptions>(config.GetSection(SearchOptions.Section));
        services.PostConfigure<SearchOptions>(o =>
        {
            o.LocalEnabled = rag.Enabled;
            o.WebEnabled = !string.IsNullOrWhiteSpace(webSearch.ApiKey);
        });

        return services;
    }
}
