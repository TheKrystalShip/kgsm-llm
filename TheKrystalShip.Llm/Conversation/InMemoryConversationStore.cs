using System.Collections.Concurrent;

using Microsoft.Extensions.Options;

using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Llm.Conversation;

/// <summary>
/// In-memory <see cref="IConversationStore"/>. Conversations are keyed by an
/// opaque conversation id, trimmed to a rolling window, and reset after idle
/// time. Thread-safe: handlers may run on worker threads.
/// </summary>
public class InMemoryConversationStore : IConversationStore
{
    private sealed class Conversation
    {
        public List<LlmMessage> Messages { get; } = new();
        public DateTime LastActivityUtc { get; set; }
    }

    private readonly ConcurrentDictionary<string, Conversation> _conversations = new();
    private readonly ConversationOptions _options;

    public InMemoryConversationStore(IOptions<ConversationOptions> options)
    {
        _options = options.Value;
    }

    public IReadOnlyList<LlmMessage> GetHistory(string conversationId)
    {
        if (!_conversations.TryGetValue(conversationId, out var conversation))
            return Array.Empty<LlmMessage>();

        lock (conversation)
        {
            if (IsIdle(conversation))
            {
                conversation.Messages.Clear();
                return Array.Empty<LlmMessage>();
            }

            return conversation.Messages.ToArray();
        }
    }

    public void Append(string conversationId, params LlmMessage[] messages)
    {
        if (messages.Length == 0)
            return;

        var conversation = _conversations.GetOrAdd(conversationId, _ => new Conversation());

        lock (conversation)
        {
            if (IsIdle(conversation))
                conversation.Messages.Clear();

            conversation.Messages.AddRange(messages);

            // Trim oldest messages beyond the rolling window.
            var overflow = conversation.Messages.Count - _options.MaxMessages;
            if (overflow > 0)
                conversation.Messages.RemoveRange(0, overflow);

            conversation.LastActivityUtc = DateTime.UtcNow;
        }
    }

    private bool IsIdle(Conversation conversation)
    {
        if (conversation.Messages.Count == 0)
            return false;

        var idleFor = DateTime.UtcNow - conversation.LastActivityUtc;
        return idleFor > TimeSpan.FromMinutes(_options.IdleTimeoutMinutes);
    }
}
