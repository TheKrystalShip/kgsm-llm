using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.Llm.Agent;
using TheKrystalShip.Llm.Backends;
using TheKrystalShip.Llm.Backends.LlamaCpp;
using TheKrystalShip.Llm.Backends.Ollama;
using TheKrystalShip.Llm.Conversation;
using TheKrystalShip.Llm.Interfaces;

namespace TheKrystalShip.Llm.Extensions;

/// <summary>
/// Registration helpers for the local-LLM stack.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the LLM client for the configured backend, the durable SQLite conversation store,
    /// and the agent loop. Options are bound from the configuration sections
    /// "Llm", "Conversation", and "LlmAgent".
    ///
    /// <c>Llm:Provider</c> selects which inference server answers — Ollama or llama.cpp. It is the
    /// only place the choice appears: the agent loop, the compactor and every tool see
    /// <see cref="ILlmClient"/> and nothing below it.
    ///
    /// The consumer MUST additionally register an <see cref="IToolDispatcher"/>
    /// (the application's tool implementations); the agent depends on it. Tools,
    /// system prompt, and per-call policy are supplied per turn via
    /// <see cref="Models.AgentTurn"/>.
    /// </summary>
    public static IServiceCollection AddLocalLlm(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var backendSection = configuration.GetSection(LlmBackendOptions.Section);

        services.Configure<LlmBackendOptions>(backendSection);
        services.Configure<LlamaCppOptions>(configuration.GetSection(LlamaCppOptions.Section));
        services.Configure<ConversationOptions>(configuration.GetSection(ConversationOptions.Section));
        services.Configure<LlmAgentOptions>(configuration.GetSection(LlmAgentOptions.Section));

        // Read once at registration: a backend swap is a restart, and resolving it per request
        // would mean holding two clients open against two servers to no purpose.
        var provider = backendSection.GetValue("Provider", LlmProvider.LlamaCpp);

        switch (provider)
        {
            case LlmProvider.Ollama:
                services.AddSingleton<ILlmClient, OllamaLlmClient>();
                break;
            default:
                services.AddSingleton<ILlmClient, LlamaCppLlmClient>();
                break;
        }

        // The canonical conversation history: SQLite-backed, append-only (per-turn deltas + checkpoints).
        // It is BOTH the model's continuity memory AND the durable record mined for self-improvement —
        // one log, never trimmed. Path comes from ConversationOptions.DatabasePath (a host points it at
        // its state dir). The old separate JSONL recorder is retired; the agent loop writes turns here.
        services.AddSingleton<IConversationStore, SqliteConversationStore>();
        services.AddSingleton<ILlmAgent, LlmAgent>();
        services.AddSingleton<IConversationCompactor, ConversationCompactor>();

        return services;
    }
}
