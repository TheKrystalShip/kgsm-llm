using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using TheKrystalShip.Kgsm.Assistant.Blueprints;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Kgsm.Assistant.Status;
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
        // The per-turn progress narration sink  — same ambient-scope shape as
        // IConfirmationContext above, registered here so it's visible to both ServerAssistant (this
        // project) and the Infrastructure aggregators that report through it (e.g.
        // BlueprintAuthoringAggregator), without Infrastructure needing anything beyond the reference
        // it already has to this project.
        services.AddSingleton<ITurnProgress, TurnProgress>();
        // How long a confirmed lifecycle command is watched for its run-state postcondition before the
        // outcome is reported as unsettled. TryAdd, so a host (or a test) can substitute a shorter
        // window without waiting out the real one.
        services.TryAddSingleton(SettlementTiming.Default);
        services.AddSingleton<IToolDispatcher, ToolDispatcher>();
        // The hot-editable prompt/tool-description layer (off unless Prompts:Directory is set). Used
        // by the prompt builder (segments) and the assistant (tool-description overlay).
        services.TryAddSingleton<IPromptOverrides, FilePromptOverrides>();
        services.AddSingleton<ISystemPromptBuilder, SystemPromptBuilder>();
        // The relevance seam: a deliberate no-op today (coarse/bulk tools are the
        // small-model fix). A host can override with its own filter before this call.
        services.TryAddSingleton<IToolRelevanceFilter, NoopToolRelevanceFilter>();
        // web_search degrades closed if no provider is wired: a host that wants real search
        // registers a concrete IWebSearch adapter (e.g. AddHttpClient<IWebSearch, TavilyWebSearch>)
        // and that later registration is the one resolved. Without one, searches fail cleanly
        // rather than breaking DI — keeps the lib embeddable by a host that doesn't use search.
        services.TryAddSingleton<IWebSearch, DisabledWebSearch>();
        // fetch_url (reading ONE specific page) degrades closed the same way: a host that wants real
        // fetching calls AddKgsmAdapters, which registers a concrete IWebFetch (e.g.
        // AddHttpClient<IWebFetch, HttpWebFetch>) that wins over this default. Without one, fetches
        // fail cleanly and the tool is omitted (FetchOptions.Available), never a dead tool.
        services.TryAddSingleton<IWebFetch, DisabledWebFetch>();
        // Local RAG retrieval degrades closed the same way: a host that enables RAG calls
        // AddKgsmAdapters (with Rag:Enabled=true), which registers a concrete IRetrieval that
        // wins over this default. Without it, retrieval fails cleanly rather than breaking DI.
        services.TryAddSingleton<IRetrieval, DisabledRetrieval>();
        // The unified `search` capability the model sees: a deterministic composer over the two
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
        // Router/UPnP visibility/control (the get_network router axis, and open_ports' opt-in router leg)
        // degrades closed independently of the firewall: a host that wires the kgsm-watchdog calls
        // AddKgsmAdapters, which registers a concrete IUpnpInfo that wins over this default. Without it,
        // get_network honestly reports router forwarding as unknown and an opted-in open_ports router leg
        // honestly reports the watchdog unavailable — never breaking DI.
        services.TryAddSingleton<IUpnpInfo, UnavailableUpnpInfo>();
        // Engine event history (the events tool) degrades closed the same way: a
        // host that wires an engine calls AddKgsmAdapters, which registers a concrete
        // IEventHistory that wins over this default. Without it, both tools honestly report the
        // journal as unreadable rather than breaking DI.
        services.TryAddSingleton<IEventHistory, UnavailableEventHistory>();
        // Per-instance facts (backups, versions, player presence, the autostart set, the console ring)
        // and host facts (uptime/load/memory/disk/ports) degrade closed the same way: a host that wires
        // an engine calls AddKgsmAdapters, which registers concrete adapters that win over these
        // defaults. Without them the reads honestly report their authority as unavailable rather than
        // breaking DI — and an empty reading is never mistaken for an idle host.
        services.TryAddSingleton<IServerFacts, UnavailableServerFacts>();
        services.TryAddSingleton<IHostFacts, UnavailableHostFacts>();
        // Blueprint field synthesis: the LLM reads the fetched research pages and extracts the native
        // server fields (the capable path the deterministic regex extractor is the fallback from). Needs
        // ILlmClient, which AddLocalLlm — required by this method, same as ServerAssistant below —
        // provides. A composition with no model can register DisabledBlueprintSynthesizer first; then
        // research always uses the regex extractor.
        services.TryAddSingleton<IBlueprintSynthesizer, LlmBlueprintSynthesizer>();
        // Blueprint repair: after a drafted config test-installs but fails to boot, the LLM reads the REAL
        // install tree + shipped launch scripts + the boot log and proposes corrected launch fields for
        // the next attempt — the evidence-driven counterpart to a blind retry. Needs ILlmClient (same as
        // synthesis). A composition with no model can register DisabledBlueprintRepair first; then the
        // pipeline runs a single attempt with no evidence-driven correction.
        services.TryAddSingleton<IBlueprintRepair, LlmBlueprintRepair>();
        // The blueprint-authoring research step is agentic: a bounded research sub-loop drives its own
        // search + fetch_url calls to gather the authoritative native-server pages, then synthesizes
        // sourced fields. The fixed one-query pass (BlueprintResearchAggregator) is registered as its
        // concrete fallback — used when the model is unavailable or the loop gathers nothing. Both
        // degrade along with whatever the search/fetch/synthesizer ports resolve to (real or disabled).
        services.AddSingleton<BlueprintResearchAggregator>();
        services.AddSingleton<IBlueprintResearch, AgenticBlueprintResearch>();
        // The admin "attempted" stash degrades closed the same way: a host that sets
        // BlueprintAuthoring:StashDir calls AddKgsmAdapters, which registers a concrete filesystem
        // store that wins over this default. Without it, a failed attempt is simply not recorded rather
        // than breaking DI.
        services.TryAddSingleton<IBlueprintAttemptStore, NullBlueprintAttemptStore>();
        // create_blueprint (the whole authoring pipeline) degrades closed the same way as fetch_url: a
        // host that enables BlueprintAuthoring:Enabled calls AddKgsmAdapters, which registers the
        // concrete aggregator (needs kgsm-lib's write-side blueprint/instance authorities, so it lives
        // in Infrastructure) that wins over this default. Without it, create_blueprint is not offered
        // (BlueprintAuthoringFlags.Available) and a stray call reports itself as not configured.
        services.TryAddSingleton<IBlueprintAuthoring, DisabledBlueprintAuthoring>();
        services.AddSingleton<IServerAssistant, ServerAssistant>();

        return services;
    }
}
