using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Cli;

/// <summary>
/// The interactive read-eval-print loop (entered when stdin is a TTY and no prompt was given).
/// One conversation id per session gives in-session memory; <c>/new</c> starts a fresh one and
/// <c>/compact</c> summarizes it in place to free context. Prompt + meta lines go to <b>stderr</b>
/// so stdout stays the assistant's reply. Ctrl-C during a turn cancels just that turn (back to the
/// prompt); Ctrl-D / <c>/exit</c> leave.
/// <para>
/// The commands come from <see cref="ChatCommands"/>, the one catalog every assistant surface reads,
/// so a command is spelled and described the same way here as it is in the web composer. Only
/// <c>/exit</c> and <c>/quit</c> are local to this surface: they mean "leave the process", which no
/// web surface can honour, so they are not in the catalog.
/// </para>
/// </summary>
internal static class Repl
{
    private const string ExitCommand = "/exit";
    private const string QuitCommand = "/quit";

    public static async Task<int> RunAsync(
        CliRunner runner, TurnInterruptor interruptor, IConversationCompactor compactor,
        IConversationStore store, IReadOnlyList<LlmToolDefinition> tools,
        bool canPerformActions, bool color)
    {
        // Somebody at this terminal already has shell access to the host and could drive kgsm directly,
        // so the tier the catalog is filtered against is the one the CLI can honestly honour: admin when
        // this session may act at all, viewer when it may not. Claiming less would hide a command this
        // surface does implement.
        var tier = canPerformActions ? KgsmTier.Admin : KgsmTier.Viewer;
        var available = ChatCommands.For(tier);

        Console.Error.WriteLine(Ansi.Paint(
            $"kgsm-assistant — interactive{(canPerformActions ? "" : " (read-only)")}. " +
            $"Type /help for commands, {ExitCommand} to quit.", Ansi.Dim, color));

        var conversationId = NewConversationId();
        store.CreateConversation(conversationId);
        var prompt = Ansi.Paint("❯ ", Ansi.Cyan, color);

        while (true)
        {
            Console.Error.Write(prompt);
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

            if (input is ExitCommand or QuitCommand)
                return 0;

            // A leading slash is only a command when the catalog names one. Anything else — a path, a
            // fraction, a typo — is an ordinary message and goes to the model, so nothing a person
            // types is silently swallowed.
            if (TrySplitCommand(input, out var verb, out var argument)
                && ChatCommands.Find(verb) is { } command)
            {
                if (tier < command.Gate)
                {
                    Console.Error.WriteLine(Ansi.Paint(
                        $"(/{command.Name} needs {KgsmTiers.ToWire(command.Gate)})", Ansi.Dim, color));
                    continue;
                }

                if (!ChatCommands.TryReadState(argument, out var requested))
                {
                    Console.Error.WriteLine(Ansi.Paint(
                        $"(/{command.Name} takes {ChatCommands.On} or {ChatCommands.Off}, "
                        + "or nothing at all to toggle it)", Ansi.Dim, color));
                    continue;
                }

                conversationId = await RunCommandAsync(
                    command, requested, runner, interruptor, compactor, store, tools, available,
                    conversationId, color);
                continue;
            }

            var (completed, _) = await interruptor.RunAsync(
                ct => runner.RunTurnAsync(conversationId, input, ct));
            if (!completed)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine(Ansi.Paint("(cancelled)", Ansi.Dim, color));
            }
        }
    }

    /// <summary>
    /// Splits <c>/verb argument</c>. Returns false when the line does not open with a slash, which is
    /// what keeps an ordinary message out of the command path.
    /// </summary>
    private static bool TrySplitCommand(string input, out string verb, out string? argument)
    {
        verb = string.Empty;
        argument = null;
        if (input.Length < 2 || input[0] != '/')
            return false;

        var body = input[1..];
        var space = body.IndexOf(' ');
        if (space < 0)
        {
            verb = body;
            return true;
        }

        verb = body[..space];
        argument = body[(space + 1)..].Trim();
        if (argument.Length == 0)
            argument = null;
        return true;
    }

    /// <summary>
    /// Runs one catalog command and answers the conversation the REPL continues in — the same id for
    /// every command but <c>/new</c>, which is the one that changes it.
    /// </summary>
    private static async Task<string> RunCommandAsync(
        ChatCommand command, bool? requested, CliRunner runner, TurnInterruptor interruptor,
        IConversationCompactor compactor, IConversationStore store, IReadOnlyList<LlmToolDefinition> tools,
        IReadOnlyList<ChatCommand> available, string conversationId, bool color)
    {
        switch (command.Name)
        {
            case "help":
                WriteHelp(available, color);
                break;

            case "tools":
                WriteTools(tools, color);
                break;

            case "new":
                conversationId = NewConversationId();
                store.CreateConversation(conversationId);
                Console.Error.WriteLine(Ansi.Paint("(new conversation)", Ansi.Dim, color));
                break;

            case "compact":
                await CompactAsync(compactor, interruptor, conversationId, color);
                break;

            case "think":
            {
                var next = requested ?? !runner.Think;
                runner.Think = next;
                store.SetPreferences(conversationId, new ConversationPreferences(next, null));
                Console.Error.WriteLine(Ansi.Paint(
                    $"thinking: {(next ? ChatCommands.On : ChatCommands.Off)}", Ansi.Dim, color));
                break;
            }

            case "autorun":
            {
                var next = requested ?? !runner.AutoRun;
                runner.AutoRun = next;
                store.SetPreferences(conversationId, new ConversationPreferences(null, next));
                Console.Error.WriteLine(Ansi.Paint(next
                    ? "auto-run: on — actions run without asking"
                    : "auto-run: off — you'll be asked to confirm each action", Ansi.Dim, color));
                break;
            }
        }

        return conversationId;
    }

    private static void WriteHelp(IReadOnlyList<ChatCommand> available, bool color)
    {
        // Widest name + its value list, so the descriptions line up whatever the catalog holds.
        var forms = available.ToDictionary(c => c.Name, Spelling, StringComparer.Ordinal);
        var width = Math.Max(
            forms.Values.Max(f => f.Length),
            Math.Max(ExitCommand.Length + QuitCommand.Length + 2, "Ctrl-C".Length));

        Console.Error.WriteLine("commands:");
        foreach (var command in available)
            Console.Error.WriteLine($"  {forms[command.Name].PadRight(width)}  {command.Description}");

        Console.Error.WriteLine($"  {$"{ExitCommand}, {QuitCommand}".PadRight(width)}  leave");
        Console.Error.WriteLine("keys:");
        Console.Error.WriteLine($"  {"Ctrl-C".PadRight(width)}  cancel the current reply (stays in the REPL)");
        Console.Error.WriteLine($"  {"Ctrl-D".PadRight(width)}  leave");
        Console.Error.WriteLine();
        _ = color;
    }

    /// <summary>How a command is typed, including the values it offers: <c>/think [on|off]</c>.</summary>
    private static string Spelling(ChatCommand command)
    {
        var values = command.Options
            .Where(o => o.Values is { Count: > 0 })
            .Select(o => $"[{string.Join('|', o.Values!)}]");
        return string.Join(' ', new[] { "/" + command.Name }.Concat(values));
    }

    private static void WriteTools(IReadOnlyList<LlmToolDefinition> tools, bool color)
    {
        if (tools.Count == 0)
        {
            Console.Error.WriteLine(Ansi.Paint("(no tools available)", Ansi.Dim, color));
            return;
        }

        var width = tools.Max(t => t.Name.Length);
        Console.Error.WriteLine("tools:");
        foreach (var tool in tools.OrderBy(t => t.Name, StringComparer.Ordinal))
            Console.Error.WriteLine($"  {tool.Name.PadRight(width)}  {FirstSentence(tool.Description)}");
        Console.Error.WriteLine();
    }

    // Tool descriptions are written for the model and run several sentences; the terminal wants the
    // first one. Truncation is by sentence rather than by width so a line never stops mid-word.
    private static string FirstSentence(string description)
    {
        var stop = description.IndexOf('.');
        return stop < 0 ? description : description[..(stop + 1)];
    }

    /// <summary>
    /// Summarizes the current conversation in place (Ctrl-C cancellable, like a turn). Status to
    /// stderr; the history is left untouched on cancel or failure. Same conversation id continues.
    /// </summary>
    private static async Task CompactAsync(
        IConversationCompactor compactor, TurnInterruptor interruptor, string conversationId, bool color)
    {
        Console.Error.WriteLine(Ansi.Paint("(compacting…)", Ansi.Dim, color));

        var (completed, result) = await interruptor.RunAsync(
            ct => compactor.CompactAsync(conversationId, ct));

        if (!completed)
        {
            Console.Error.WriteLine(Ansi.Paint("(cancelled)", Ansi.Dim, color));
            return;
        }
        if (result.IsFailure)
        {
            Console.Error.WriteLine(Ansi.Paint($"(compaction failed: {result.Error})", Ansi.Red, color));
            return;
        }

        var outcome = result.Value!;
        Console.Error.WriteLine(Ansi.Paint(outcome.Compacted
            ? $"(compacted {outcome.MessagesCompacted} messages into a summary)"
            : "(nothing to compact yet)", Ansi.Dim, color));
    }

    private static string NewConversationId() => CliConversation.NewId();
}
