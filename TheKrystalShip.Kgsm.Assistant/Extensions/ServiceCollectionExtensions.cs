using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Llm.Interfaces;

namespace TheKrystalShip.Kgsm.Assistant.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the kgsm assistant: the system-prompt builder, the per-turn
    /// confirmation sink, the tool dispatcher (as the library's
    /// <see cref="IToolDispatcher"/>), and the policy-bearing
    /// <see cref="IServerAssistant"/>.
    /// <para>
    /// The host must separately register the reusable LLM stack
    /// (<c>AddLocalLlm</c>) and an implementation of the
    /// <see cref="Ports.IServerOperations"/> and <see cref="Ports.IServerInventory"/>
    /// ports.
    /// </para>
    /// </summary>
    public static IServiceCollection AddKgsmAssistant(this IServiceCollection services)
    {
        services.AddSingleton<IConfirmationContext, ConfirmationContext>();
        services.AddSingleton<IToolDispatcher, ToolDispatcher>();
        // The hot-editable prompt/tool-description layer (off unless Prompts:Directory is set). Used
        // by the prompt builder (segments) and the assistant (tool-description overlay).
        services.TryAddSingleton<IPromptOverrides, FilePromptOverrides>();
        services.AddSingleton<ISystemPromptBuilder, SystemPromptBuilder>();
        // The §3.2 relevance seam: a deliberate no-op today (coarse/bulk tools are the
        // small-model fix). A host can override with its own filter before this call.
        services.TryAddSingleton<IToolRelevanceFilter, NoopToolRelevanceFilter>();
        // web_search degrades closed if no provider is wired: a host that wants real search
        // registers a concrete IWebSearch adapter (e.g. AddHttpClient<IWebSearch, TavilyWebSearch>)
        // and that later registration is the one resolved. Without one, searches fail cleanly
        // rather than breaking DI — keeps the lib embeddable by a host that doesn't use search.
        services.TryAddSingleton<IWebSearch, DisabledWebSearch>();
        // Local RAG retrieval degrades closed the same way: a host that enables RAG calls
        // AddKgsmAdapters (with Rag:Enabled=true), which registers a concrete IRetrieval that
        // wins over this default. Without it, retrieval fails cleanly rather than breaking DI.
        services.TryAddSingleton<IRetrieval, DisabledRetrieval>();
        // The unified `search` capability the model sees (§3.4): a deterministic composer over the two
        // ports above (local docs first → web fallback). Always registered; whether the `search` TOOL
        // is offered is a separate, config-driven decision (SearchOptions.Available, set by the host).
        services.AddSingleton<ISearch, SearchAggregator>();
        // Live per-server metrics degrade closed the same way: a host that wires the kgsm-monitor
        // calls AddKgsmAdapters, which registers a concrete IServerMetrics that wins over this
        // default. Without it, get_performance honestly reports the monitor as unavailable rather
        // than breaking DI — keeps the assistant embeddable by a host with no monitor.
        services.TryAddSingleton<IServerMetrics, UnavailableServerMetrics>();
        // Host-firewall visibility/control (get_network / open_ports) degrades closed the same way: a
        // host that wires the kgsm-firewall authority calls AddKgsmAdapters, which registers a concrete
        // INetworkInfo that wins over this default. Without it, get_network honestly reports the firewall
        // as unavailable and a confirmed open_ports honestly reports "nothing changed" rather than
        // breaking DI — keeps the assistant embeddable by a host with no firewall authority.
        services.TryAddSingleton<INetworkInfo, UnavailableNetworkInfo>();
        // Engine event history (get_audit_log / get_change_timeline) degrades closed the same way: a
        // host that wires the kgsm-monitor calls AddKgsmAdapters, which registers a concrete
        // IEventHistory that wins over this default. Without it, both tools honestly report the
        // monitor as unavailable rather than breaking DI.
        services.TryAddSingleton<IEventHistory, UnavailableEventHistory>();
        services.AddSingleton<IServerAssistant, ServerAssistant>();

        return services;
    }
}
