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

    /// <summary>Every turn appended, in order.</summary>
    public List<ConversationTurnRecord> Turns { get; } = new();

    /// <summary>Every checkpoint summary appended, in order.</summary>
    public List<string> Checkpoints { get; } = new();

    public IReadOnlyList<ConversationEntry> GetHistory(string conversationId) => _entries.ToArray();

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
        _entries.Add(ConversationEntry.ForTurn(turn));
    }

    public void AddCheckpoint(string conversationId, string summary)
    {
        Checkpoints.Add(summary);
        _entries.Add(ConversationEntry.ForCheckpoint(summary, DateTimeOffset.UtcNow));
    }

    /// <summary>The model-replay projection across all stored turns — the view the agent persists into.</summary>
    public IReadOnlyList<LlmMessage> Messages => GetModelContext(string.Empty);
}
