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
        services.AddSingleton<IServerAssistant, ServerAssistant>();

        return services;
    }
}
