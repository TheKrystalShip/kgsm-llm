namespace TheKrystalShip.Llm.Conversation;

/// <summary>
/// Resolves the <b>owner</b> a conversation's memories belong to: the conversation id up to its second
/// <c>:</c>, or the whole id when it carries no second one.
/// <para>
/// This is what makes memory cross a conversation boundary without crossing a person's. A per-chat id
/// (<c>web:alice:chat7</c>) resolves to its owner (<c>web:alice</c>), so something written in one chat
/// is read back in the next; a bare per-user id resolves to itself; and an id keyed to a place rather
/// than a person (<c>room:g1-v42</c>) resolves to the whole thing — a room's memory belongs to the
/// room, because there is no verified user segment in it to anchor to and a per-person memory keyed
/// inside a shared transcript would put everybody alone in a place they thought they were sharing.
/// </para>
/// <para>
/// The same rule is computed in SQL by <see cref="SqliteConversationStore"/> to derive a
/// <see cref="Models.ConversationActor"/> — a query cannot call this method, so the rule necessarily
/// lives in two languages. <c>MemoryScopeTests</c> pins them to each other; change one and that test
/// is what says the other must move too.
/// </para>
/// </summary>
public static class MemoryScope
{
    private const char Separator = ':';

    /// <summary>
    /// The owner key <paramref name="conversationId"/> belongs to. A blank id answers empty rather
    /// than throwing: the caller reads that as "this turn has no owner", which refuses every memory
    /// tool — the one safe reading, since the alternative is writing into a namespace shared by
    /// everyone whose id could not be resolved.
    /// </summary>
    public static string OwnerOf(string? conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
            return string.Empty;

        var trimmed = conversationId.Trim();

        var first = trimmed.IndexOf(Separator);
        if (first < 0)
            return trimmed;

        var second = trimmed.IndexOf(Separator, first + 1);
        return second < 0 ? trimmed : trimmed[..second];
    }
}
