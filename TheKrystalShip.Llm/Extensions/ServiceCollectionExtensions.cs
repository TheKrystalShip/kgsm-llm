using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.Llm.Agent;
using TheKrystalShip.Llm.Conversation;
using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Ollama;
using TheKrystalShip.Llm.Recording;

namespace TheKrystalShip.Llm.Extensions;

/// <summary>
/// Registration helpers for the local-LLM stack.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Ollama-backed LLM client, an in-memory conversation store,
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
        services.Configure<RecordingOptions>(configuration.GetSection(RecordingOptions.Section));

        services.AddSingleton<ILlmClient, OllamaLlmClient>();
        // Durable conversation memory: SQLite-backed so a conversation's rolling context survives a
        // restart. Path comes from ConversationOptions.DatabasePath (a host points it at its state dir).
        services.AddSingleton<IConversationStore, SqliteConversationStore>();
        services.AddSingleton<ILlmAgent, LlmAgent>();
        services.AddSingleton<IConversationCompactor, ConversationCompactor>();

        // Conversation recording is a separate, append-only sink for offline self-improvement
        // analysis — NOT the model's working memory. Off by default; a host opts in and supplies a
        // writable directory. When disabled (or directory-less) the no-op keeps the agent loop's
        // record-building short-circuited. Installed here so every surface inherits it uniformly.
        var recording = configuration.GetSection(RecordingOptions.Section).Get<RecordingOptions>() ?? new RecordingOptions();
        if (recording.Enabled && !string.IsNullOrWhiteSpace(recording.Directory))
            services.AddSingleton<IConversationRecorder, JsonlConversationRecorder>();
        else
            services.AddSingleton<IConversationRecorder, NoopConversationRecorder>();

        return services;
    }
}
