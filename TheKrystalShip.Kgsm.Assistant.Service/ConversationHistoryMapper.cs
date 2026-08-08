using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Service;

/// <summary>
/// Projects the store's canonical <see cref="ConversationEntry"/> log into the read-back DTOs
/// (<c>GET /conversations</c> / <c>GET /conversations/{id}</c>). Pure mapping — no storage, no auth; the
/// endpoint owns the principal-scoped key derivation. The turn projection keeps the §5·a field names
/// (tool/thinking/usage) so a client re-scaffolds an old conversation through its live-turn render path.
/// </summary>
internal static class ConversationHistoryMapper
{
    /// <summary>
    /// The client-facing chat id = the per-chat sub-scope after <c>web:{userId}:</c>. Empty for the bare
    /// per-user conversation (<c>web:{userId}</c>), which predates the per-chat split.
    /// </summary>
    public static string ChatIdOf(string conversationId, string userId)
    {
        var prefix = $"web:{userId}:";
        return conversationId.StartsWith(prefix, StringComparison.Ordinal)
            ? conversationId[prefix.Length..]
            : string.Empty;
    }

    /// <param name="s">The stored index entry.</param>
    /// <param name="userId">The verified caller, whose prefix is stripped to give the client-facing id.</param>
    /// <param name="thinkDefault">
    /// What thinking falls back to when the conversation has never set it — the host's configured
    /// value, so the listing states what the next turn would do rather than leaving the client to guess
    /// what an unset switch means. Auto-run's floor is false: nothing else is safe to assume of a
    /// conversation nobody has armed.
    /// </param>
    public static ConversationSummaryDto ToSummaryDto(ConversationSummary s, string userId, bool thinkDefault) =>
        new(ChatIdOf(s.ConversationId, userId), s.Title, s.CreatedAt, s.LastActivityAt, s.TurnCount,
            s.Preferences.Think ?? thinkDefault,
            s.Preferences.Autorun ?? false);

    public static ConversationHistoryEntryDto ToEntryDto(ConversationEntry e) =>
        e.Kind == ConversationEntryKind.Checkpoint
            ? new ConversationHistoryEntryDto("checkpoint", e.CreatedAt, CheckpointSummary: e.CheckpointSummary)
            : new ConversationHistoryEntryDto(
                "turn", e.CreatedAt, Turn: ToTurnDto(e.Turn!), StartedAt: e.Turn!.StartedAt,
                // The id is what makes a REPLAYED answer ratable, not just a live one — and history is
                // the bulk of the corpus, so without it the feature would only ever reach the newest turn.
                TurnId: e.Id,
                Feedback: ToFeedbackDto(e.Feedback));

    private static TurnFeedbackDto? ToFeedbackDto(TurnFeedback? f) =>
        f is null ? null : new TurnFeedbackDto(f.Rating.ToString().ToLowerInvariant(), f.Note, f.At);

    private static ConversationTurnDto ToTurnDto(ConversationTurnRecord t) =>
        new(
            t.UserPrompt,
            t.Final,
            t.Think,
            t.Thinking,
            t.Tools.Select(ToToolDto).ToArray(),
            UsageDto.From(t.Usage),
            t.Outcome.ToString().ToLowerInvariant());

    private static ConversationToolDto ToToolDto(RecordedToolCall c) =>
        new(c.Name.Name, c.Arguments, c.Summary, c.Card);
}
