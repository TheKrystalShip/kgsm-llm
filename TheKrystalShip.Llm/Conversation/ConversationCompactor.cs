using System.Text;

using Microsoft.Extensions.Logging;

using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Llm.Conversation;

/// <summary>
/// Default <see cref="IConversationCompactor"/>: reads the model's current context, asks the model for a
/// concise briefing (a plain, tool-less completion), then appends a <b>checkpoint</b> to the history so
/// later turns keep continuity on a fraction of the context. <b>Non-destructive</b> — the prior turns
/// stay in the canon history (the user still sees the whole conversation); the checkpoint only changes
/// what the assistant replays from here forward. Backend-agnostic — it depends only on
/// <see cref="ILlmClient"/> and <see cref="IConversationStore"/>.
/// </summary>
public sealed class ConversationCompactor : IConversationCompactor
{
    /// <summary>Below this, compaction isn't worth a model round-trip — there's nothing to fold.</summary>
    private const int MinMessagesToCompact = 2;

    private readonly ILlmClient _llmClient;
    private readonly IConversationStore _store;
    private readonly ILogger<ConversationCompactor> _logger;

    public ConversationCompactor(
        ILlmClient llmClient,
        IConversationStore store,
        ILogger<ConversationCompactor> logger)
    {
        _llmClient = llmClient;
        _store = store;
        _logger = logger;
    }

    public async Task<Result<CompactionOutcome>> CompactAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        // The model's CURRENT context (latest checkpoint summary + turns since) — what we fold into a
        // fresh checkpoint. The full history on disk is untouched.
        var context = _store.GetModelContext(conversationId);
        if (context.Count < MinMessagesToCompact)
            return Result.Success(CompactionOutcome.Nothing());

        // A tool-less completion: summarizing never calls tools. System instruction + the
        // rendered transcript as a single user message.
        var request = new List<LlmMessage>
        {
            LlmMessage.System(SummarizerPrompt),
            LlmMessage.User(RenderTranscript(context)),
        };

        var response = await _llmClient.ChatAsync(request, tools: null, think: false, cancellationToken);
        if (response.IsFailure)
            return Result.Failure<CompactionOutcome>(response.Error!);

        var summary = (response.Value!.Content ?? string.Empty).Trim();
        if (summary.Length == 0)
            return Result.Failure<CompactionOutcome>("the model returned an empty summary.");

        // Append a checkpoint (non-destructive): prior turns remain in the canon history; the model
        // replays from this summary forward. The store wraps the raw summary as a recap message when it
        // projects the model context, so we store the bare summary here.
        _store.AddCheckpoint(conversationId, summary);

        _logger.LogDebug(
            "Checkpointed {Count} context messages for conversation {Conversation}", context.Count, conversationId);
        return Result.Success(CompactionOutcome.Done(context.Count, summary));
    }

    /// <summary>
    /// Renders the stored history into a plain "Role: text" transcript for the summarizer. The
    /// agent's persistence boundary keeps only user text + final assistant text in the store, so
    /// every message here carries meaningful <see cref="LlmMessage.Content"/>.
    /// </summary>
    private static string RenderTranscript(IReadOnlyList<LlmMessage> history)
    {
        var sb = new StringBuilder();
        foreach (var m in history)
        {
            var who = m.Role switch
            {
                LlmRole.User => "User",
                LlmRole.Assistant => "Assistant",
                LlmRole.Tool => $"Tool({m.ToolName})",
                _ => m.Role.ToString(),
            };
            sb.Append(who).Append(": ").AppendLine(m.Content);
        }
        return sb.ToString().TrimEnd();
    }

    private const string SummarizerPrompt =
        """
        You compress conversations to save context. Summarize the conversation below into a
        concise briefing that can REPLACE the full history while preserving continuity.

        Capture, as compact notes:
        - the user's goals and any open questions
        - concrete facts established (server/instance names, statuses, settings, results)
        - decisions made and actions taken or still pending
        - anything unresolved the next reply may need

        Omit greetings and filler. Do not answer, continue, or invent anything — only summarize
        what was already said. Output only the summary text.
        """;
}
