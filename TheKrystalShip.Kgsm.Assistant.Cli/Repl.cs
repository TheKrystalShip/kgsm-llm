using TheKrystalShip.Llm.Interfaces;

namespace TheKrystalShip.Kgsm.Assistant.Cli;

/// <summary>
/// The interactive read-eval-print loop (entered when stdin is a TTY and no prompt was given).
/// One conversation id per session gives in-session memory; <c>/reset</c> starts a fresh one and
/// <c>/compact</c> summarizes it in place to free context. Prompt + meta lines go to <b>stderr</b>
/// so stdout stays the assistant's reply. Ctrl-C during a turn cancels just that turn (back to the
/// prompt); Ctrl-D / <c>/exit</c> leave.
/// </summary>
internal static class Repl
{
    private const string HelpText =
        """
        commands:
          /exit, /quit   leave
          /reset         start a fresh conversation (clears in-session memory)
          /compact       summarize this conversation in place to free up context
          /help          show this help
        keys:
          Ctrl-C         cancel the current reply (stays in the REPL)
          Ctrl-D         leave

        """;

    public static async Task<int> RunAsync(
        CliRunner runner, TurnInterruptor interruptor, IConversationCompactor compactor, bool canPerformActions)
    {
        Console.Error.WriteLine(
            $"kgsm-assistant — interactive{(canPerformActions ? "" : " (read-only)")}. " +
            "Type /help for commands, /exit to quit.");

        var conversationId = NewConversationId();

        while (true)
        {
            Console.Error.Write("> ");
            Console.Error.Flush();

            var line = Console.In.ReadLine();
            if (line is null)   // Ctrl-D / EOF
            {
                Console.Error.WriteLine();
                return 0;
            }

            var input = line.Trim();
            if (input.Length == 0)
                continue;

            switch (input)
            {
                case "/exit":
                case "/quit":
                    return 0;
                case "/help":
                    Console.Error.Write(HelpText);
                    continue;
                case "/reset":
                    conversationId = NewConversationId();
                    Console.Error.WriteLine("(new conversation)");
                    continue;
                case "/compact":
                    await CompactAsync(compactor, interruptor, conversationId);
                    continue;
            }

            var (completed, _) = await interruptor.RunAsync(
                ct => runner.RunTurnAsync(conversationId, input, ct));
            if (!completed)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("(cancelled)");
            }
        }
    }

    /// <summary>
    /// Summarizes the current conversation in place (Ctrl-C cancellable, like a turn). Status to
    /// stderr; the history is left untouched on cancel or failure. Same conversation id continues.
    /// </summary>
    private static async Task CompactAsync(
        IConversationCompactor compactor, TurnInterruptor interruptor, string conversationId)
    {
        Console.Error.WriteLine("(compacting…)");

        var (completed, result) = await interruptor.RunAsync(
            ct => compactor.CompactAsync(conversationId, ct));

        if (!completed)
        {
            Console.Error.WriteLine("(cancelled)");
            return;
        }
        if (result.IsFailure)
        {
            Console.Error.WriteLine($"(compaction failed: {result.Error})");
            return;
        }

        var outcome = result.Value!;
        Console.Error.WriteLine(outcome.Compacted
            ? $"(compacted {outcome.MessagesCompacted} messages into a summary)"
            : "(nothing to compact yet)");
    }

    private static string NewConversationId() => $"cli:{Guid.NewGuid():N}";
}
