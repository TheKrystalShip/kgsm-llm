using System.Net.Http.Headers;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Infrastructure.Configuration;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Kgsm;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Search;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Services;

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

        return services;
    }
}
