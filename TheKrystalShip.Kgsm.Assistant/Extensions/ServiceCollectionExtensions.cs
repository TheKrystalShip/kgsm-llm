using Microsoft.Extensions.DependencyInjection;

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
        services.AddSingleton<ISystemPromptBuilder, SystemPromptBuilder>();
        services.AddSingleton<IServerAssistant, ServerAssistant>();

        return services;
    }
}
