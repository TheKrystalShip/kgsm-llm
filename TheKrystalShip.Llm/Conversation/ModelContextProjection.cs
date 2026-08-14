using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Llm.Conversation;

/// <summary>
/// Projects a stored <see cref="ConversationTurnRecord"/> into the messages the model replays for it.
/// One implementation for every <see cref="Interfaces.IConversationStore"/>, so two stores cannot
/// disagree about what a past turn looked like.
/// <para>
/// A replayed turn keeps the SHAPE of what happened: the prompt, the tool calls the model made, and
/// the reply. The transcript a model reads is also a set of examples it imitates, so a turn that
/// answered "start the server" by calling a tool must replay as a tool call — replayed as prose
/// alone it teaches that the same request is answered by describing the action instead of taking
/// it, and the next reply narrates a staging that never happened.
/// </para>
/// <para>
/// Tool RESULTS are not replayed: each carries a reading of a world that has moved on, and a stale
/// reading presented as current is a fabricated status. Every call replays against
/// <see cref="ReplayedToolResult"/>, which says so and asks for a fresh call, so a follow-up
/// re-queries live data.
/// </para>
/// </summary>
public static class ModelContextProjection
{
    /// <summary>
    /// Stands in for a past call's output. The call is what the transcript needs; its result belongs
    /// to the moment it was read.
    /// </summary>
    internal const string ReplayedToolResult =
        "(from an earlier turn — the result is not replayed. Call the tool again for current data.)";

    /// <summary>
    /// How many of a turn's calls replay. The point is the shape of the turn, which the first few
    /// calls carry; a long trajectory would otherwise spend context restating a single past turn.
    /// </summary>
    private const int MaxReplayedCallsPerTurn = 8;

    /// <summary>
    /// Appends <paramref name="turn"/>'s messages to <paramref name="messages"/>: the user prompt,
    /// this turn's tool calls (each against a placeholder result), then the final reply.
    /// <paramref name="attributeSpeakers"/> labels the prompt with the display name recorded on the
    /// turn, for a conversation several people share.
    /// </summary>
    public static void AppendTurn(
        List<LlmMessage> messages, ConversationTurnRecord turn, bool attributeSpeakers)
    {
        messages.Add(attributeSpeakers
            ? SpeakerAttribution.Message(turn.UserDisplay, turn.UserPrompt)
            : LlmMessage.User(turn.UserPrompt));

        if (turn.Tools.Count > 0)
        {
            var calls = turn.Tools
                .Take(MaxReplayedCallsPerTurn)
                .Select(t => new LlmToolCall(t.Name, t.Arguments))
                .ToList();

            // One assistant turn requesting the calls, then one result message per call — the same
            // pairing the live loop feeds back, which is what makes a replayed turn read as a turn
            // that used tools rather than one that guessed.
            messages.Add(LlmMessage.AssistantToolCalls(calls));
            foreach (var call in calls)
                messages.Add(LlmMessage.Tool(call.Name, ReplayedToolResult));
        }

        if (!string.IsNullOrWhiteSpace(turn.Final))
            messages.Add(LlmMessage.Assistant(turn.Final!));
    }
}
