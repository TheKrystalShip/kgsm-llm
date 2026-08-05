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
    // Soft-deleted conversation ids (hidden from ListConversations). Latest-wins: appending content to a
    // conversation un-hides it (see Track), mirroring the real store's newest-entry rule.
    private readonly HashSet<string> _deleted = new(StringComparer.Ordinal);

    /// <summary>Every turn appended, in order.</summary>
    public List<ConversationTurnRecord> Turns { get; } = new();

    /// <summary>Every checkpoint summary appended, in order.</summary>
    public List<string> Checkpoints { get; } = new();

    public IReadOnlyList<ConversationEntry> GetHistory(string conversationId) => _entries.ToArray();

    public IReadOnlyList<ConversationSummary> ListConversations(string scopeKey, bool includeDeleted = false)
    {
        // Mirror the real store: the scope key itself OR its ":"-children, most-recently-active first.
        var prefix = scopeKey + ":";
        return _byConversation
            .Where(kv => (kv.Key == scopeKey || kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                         && (includeDeleted || !_deleted.Contains(kv.Key)))
            .Select(kv => Summarize(kv.Key, kv.Value))
            .OrderByDescending(s => s.LastActivityAt)
            .ToList();
    }

    public IReadOnlyList<ConversationActor> ListActors(string surfacePrefix)
    {
        // Mirror the real store: group by the id up to its SECOND ':' ({surface}:{user}), or the whole
        // id when it has no chat segment.
        var surface = surfacePrefix.TrimEnd(':');
        return _byConversation
            .Where(kv => kv.Key == surface || kv.Key.StartsWith(surface + ":", StringComparison.Ordinal))
            .GroupBy(kv =>
            {
                var rest = kv.Key.Length > surface.Length ? kv.Key[(surface.Length + 1)..] : string.Empty;
                var cut = rest.IndexOf(':');
                return cut < 0 ? rest : rest[..cut];
            })
            .Select(g => new ConversationActor
            {
                Surface = surface,
                UserId = g.Key,
                UserDisplay = g.SelectMany(kv => kv.Value)
                    .Where(e => e.Kind == ConversationEntryKind.Turn && e.Turn!.UserDisplay is not null)
                    .Select(e => e.Turn!.UserDisplay)
                    .LastOrDefault(),
                ConversationCount = g.Count(),
                DeletedCount = g.Count(kv => _deleted.Contains(kv.Key)),
                TurnCount = g.SelectMany(kv => kv.Value).Count(e => e.Kind == ConversationEntryKind.Turn),
                FirstActivityAt = g.Min(kv => kv.Value[0].CreatedAt),
                LastActivityAt = g.Max(kv => kv.Value[^1].CreatedAt),
            })
            .OrderByDescending(a => a.LastActivityAt)
            .ToList();
    }

    private ConversationSummary Summarize(string id, List<ConversationEntry> entries)
    {
        var turns = entries.Where(e => e.Kind == ConversationEntryKind.Turn).ToList();
        return new ConversationSummary
        {
            ConversationId = id,
            Title = turns.Count > 0 ? turns[0].Turn!.UserPrompt : null,
            CreatedAt = entries[0].CreatedAt,
            LastActivityAt = entries[^1].CreatedAt,
            TurnCount = turns.Count,
            UserDisplay = turns.LastOrDefault(t => t.Turn!.UserDisplay is not null)?.Turn!.UserDisplay,
            Deleted = _deleted.Contains(id),
            ErrorTurns = turns.Count(t => t.Turn!.Outcome == TurnOutcome.Error),
            CapHitTurns = turns.Count(t => t.Turn!.Outcome == TurnOutcome.CapHit),
        };
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

    public void SoftDelete(string conversationId) => _deleted.Add(conversationId);

    /// <summary>
    /// The roll-up is a SQL derivation over the real log; this double exists to observe what the agent
    /// loop writes, so re-deriving it here would test the double rather than the store. Callers that
    /// need real numbers use <c>SqliteConversationStore</c> against a temp file.
    /// </summary>
    public ConversationStats GetStats(string surfacePrefix) => throw new NotSupportedException(
        "TestConversationStore does not derive statistics; use SqliteConversationStore.");

    private void Track(string conversationId, ConversationEntry entry)
    {
        _deleted.Remove(conversationId);   // new content supersedes a prior soft-delete (a resume)
        if (!_byConversation.TryGetValue(conversationId, out var list))
            _byConversation[conversationId] = list = new List<ConversationEntry>();
        list.Add(entry);
    }

    /// <summary>The model-replay projection across all stored turns — the view the agent persists into.</summary>
    public IReadOnlyList<LlmMessage> Messages => GetModelContext(string.Empty);
}
