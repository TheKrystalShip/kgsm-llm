using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Llm.Tests;

/// <summary>
/// In-memory <see cref="IConversationStore"/> test double: records the appended turns and checkpoints,
/// and mirrors the real store's model-context projection. <see cref="Messages"/> is that projection —
/// what the model replays (the user prompt + final reply of each turn after the latest checkpoint) —
/// the view the agent tests assert persistence against.
/// </summary>
internal sealed class TestConversationStore : IConversationStore
{
    private readonly List<ConversationEntry> _entries = new();
    // Per-conversation entries, in append order — backs ListConversations (the agent tests use a single
    // conversation, so the flat _entries projection above stays equivalent for them).
    private readonly Dictionary<string, List<ConversationEntry>> _byConversation = new(StringComparer.Ordinal);

    /// <summary>Every turn appended, in order.</summary>
    public List<ConversationTurnRecord> Turns { get; } = new();

    /// <summary>Every checkpoint summary appended, in order.</summary>
    public List<string> Checkpoints { get; } = new();

    public IReadOnlyList<ConversationEntry> GetHistory(string conversationId) => _entries.ToArray();

    public IReadOnlyList<ConversationSummary> ListConversations(string scopeKey)
    {
        // Mirror the real store: the scope key itself OR its ":"-children, most-recently-active first.
        var prefix = scopeKey + ":";
        return _byConversation
            .Where(kv => kv.Key == scopeKey || kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(kv =>
            {
                var turns = kv.Value.Where(e => e.Kind == ConversationEntryKind.Turn).ToList();
                return new ConversationSummary
                {
                    ConversationId = kv.Key,
                    Title = turns.Count > 0 ? turns[0].Turn!.UserPrompt : null,
                    CreatedAt = kv.Value[0].CreatedAt,
                    LastActivityAt = kv.Value[^1].CreatedAt,
                    TurnCount = turns.Count,
                };
            })
            .OrderByDescending(s => s.LastActivityAt)
            .ToList();
    }

    public IReadOnlyList<LlmMessage> GetModelContext(string conversationId)
    {
        var messages = new List<LlmMessage>();
        var lastCheckpoint = _entries.FindLastIndex(e => e.Kind == ConversationEntryKind.Checkpoint);
        var start = 0;
        if (lastCheckpoint >= 0)
        {
            messages.Add(LlmMessage.Assistant(_entries[lastCheckpoint].CheckpointSummary!));
            start = lastCheckpoint + 1;
        }

        for (var i = start; i < _entries.Count; i++)
        {
            if (_entries[i].Kind != ConversationEntryKind.Turn)
                continue;
            var turn = _entries[i].Turn!;
            messages.Add(LlmMessage.User(turn.UserPrompt));
            if (!string.IsNullOrWhiteSpace(turn.Final))
                messages.Add(LlmMessage.Assistant(turn.Final!));
        }

        return messages;
    }

    public void AppendTurn(ConversationTurnRecord turn)
    {
        Turns.Add(turn);
        var entry = ConversationEntry.ForTurn(turn);
        _entries.Add(entry);
        Track(turn.ConversationId, entry);
    }

    public void AddCheckpoint(string conversationId, string summary)
    {
        Checkpoints.Add(summary);
        var entry = ConversationEntry.ForCheckpoint(summary, DateTimeOffset.UtcNow);
        _entries.Add(entry);
        Track(conversationId, entry);
    }

    private void Track(string conversationId, ConversationEntry entry)
    {
        if (!_byConversation.TryGetValue(conversationId, out var list))
            _byConversation[conversationId] = list = new List<ConversationEntry>();
        list.Add(entry);
    }

    /// <summary>The model-replay projection across all stored turns — the view the agent persists into.</summary>
    public IReadOnlyList<LlmMessage> Messages => GetModelContext(string.Empty);
}
