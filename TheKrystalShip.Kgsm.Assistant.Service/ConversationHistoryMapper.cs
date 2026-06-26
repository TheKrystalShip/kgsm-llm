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

    public static ConversationSummaryDto ToSummaryDto(ConversationSummary s, string userId) =>
        new(ChatIdOf(s.ConversationId, userId), s.Title, s.CreatedAt, s.LastActivityAt, s.TurnCount);

    public static ConversationHistoryEntryDto ToEntryDto(ConversationEntry e) =>
        e.Kind == ConversationEntryKind.Checkpoint
            ? new ConversationHistoryEntryDto("checkpoint", e.CreatedAt, CheckpointSummary: e.CheckpointSummary)
            : new ConversationHistoryEntryDto("turn", e.CreatedAt, Turn: ToTurnDto(e.Turn!));

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
