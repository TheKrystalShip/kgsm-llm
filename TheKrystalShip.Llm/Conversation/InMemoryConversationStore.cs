using System.Collections.Concurrent;

using Microsoft.Extensions.Options;

using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Llm.Conversation;

/// <summary>
/// In-memory <see cref="IConversationStore"/>. Conversations are keyed by an opaque conversation id and
/// trimmed to a rolling window. There is NO idle reset — the conversation id is the canonical scope: a
/// fresh chat means a fresh id (the assistant Service keys each SPA chat as <c>web:{userId}:{chatId}</c>),
/// so context can never leak between chats and there is nothing to "time out". A conversation is therefore
/// RETAINED for the life of the process and can be resumed from any past point as long as the caller still
/// holds its id. Thread-safe: handlers may run on worker threads.
/// </summary>
public class InMemoryConversationStore : IConversationStore
{
    private sealed class Conversation
    {
        public List<LlmMessage> Messages { get; } = new();
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
            conversation.Messages.AddRange(messages);

            // Trim oldest messages beyond the rolling window.
            var overflow = conversation.Messages.Count - _options.MaxMessages;
            if (overflow > 0)
                conversation.Messages.RemoveRange(0, overflow);
        }
    }

    public void Replace(string conversationId, params LlmMessage[] messages)
    {
        var conversation = _conversations.GetOrAdd(conversationId, _ => new Conversation());

        lock (conversation)
        {
            conversation.Messages.Clear();
            conversation.Messages.AddRange(messages);

            // Honor the same rolling window as Append (a summary message is tiny, but stay safe).
            var overflow = conversation.Messages.Count - _options.MaxMessages;
            if (overflow > 0)
                conversation.Messages.RemoveRange(0, overflow);
        }
    }
}
