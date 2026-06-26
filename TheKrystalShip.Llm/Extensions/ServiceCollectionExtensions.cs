using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.Llm.Agent;
using TheKrystalShip.Llm.Conversation;
using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Ollama;

namespace TheKrystalShip.Llm.Extensions;

/// <summary>
/// Registration helpers for the local-LLM stack.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Ollama-backed LLM client, the durable SQLite conversation store,
    /// and the agent loop. Options are bound from the configuration sections
    /// "Ollama", "Conversation", and "LlmAgent".
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
        services.Configure<OllamaOptions>(configuration.GetSection(OllamaOptions.Section));
        services.Configure<ConversationOptions>(configuration.GetSection(ConversationOptions.Section));
        services.Configure<LlmAgentOptions>(configuration.GetSection(LlmAgentOptions.Section));

        services.AddSingleton<ILlmClient, OllamaLlmClient>();
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
