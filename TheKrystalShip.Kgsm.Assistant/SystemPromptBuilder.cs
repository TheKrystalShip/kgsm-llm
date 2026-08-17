using System.Text;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant;

/// <inheritdoc />
public class SystemPromptBuilder : ISystemPromptBuilder
{
    private readonly IServerInventory _inventory;
    private readonly ILogger<SystemPromptBuilder> _logger;
    private readonly IConfiguration _configuration;
    private readonly IPromptOverrides _overrides;

    public SystemPromptBuilder(
        IServerInventory inventory,
        ILogger<SystemPromptBuilder> logger,
        IConfiguration configuration,
        IPromptOverrides overrides)
    {
        _inventory = inventory;
        _logger = logger;
        _configuration = configuration;
        _overrides = overrides;
    }

    public async Task<BuiltPrompt> BuildAsync(
        bool canPerformActions, bool autoExecute = false, CancellationToken cancellationToken = default,
        string? leaf = null, ReplyStyle style = ReplyStyle.Default)
    {
        // The editable template: persona + authorization stance. Each segment resolves
        // file (hot, top precedence) > inline Llm:* config > lib-owned constant default.
        // Three stances: denied (read-only) < allowed (propose-only) < auto (lifecycle runs now).
        var preamble = Effective(PromptSegments.Preamble, leaf);
        var actionsSegment = !canPerformActions ? PromptSegments.ActionsDenied
            : autoExecute ? PromptSegments.ActionsAuto
            : PromptSegments.ActionsAllowed;
        var actions = Effective(actionsSegment, leaf);
        var template = preamble + actions;

        // A style asked for by the caller is delivery, not persona, and it is appended AFTER the live
        // lists below rather than here — it is the one instruction that has to survive several hundred
        // lines of persona and a catalog, so it goes last, where the model reads it immediately before
        // answering. It is part of the hashed template all the same: a turn answered in a different
        // shape was built from a different prompt, and the recorded hash has to say so.
        var styleSegment = style == ReplyStyle.Voice ? Effective(PromptSegments.Voice, leaf) : string.Empty;

        // Hash ONLY the template — not the live lists appended below — so the recorded id moves when
        // the prompt is tuned, not when a server is installed mid-session.
        var templateHash = PromptHash.Short(template + styleSegment);

        var builder = new StringBuilder(template);

        // The host's clock, stated outright. A tool result is the only thing the model reads and none
        // of them carries a "now", so without this a timestamp in a reading has no frame: asked how
        // recent a backup is, the model either declines or supplies a date from its own training,
        // which is a fabrication with no tool able to contradict it. Deliberately below the hashed
        // template, for the same reason the instance list is — the prompt did not change because a
        // minute passed.
        builder.Append("\n\nRight now it is ")
               .Append(Elapsed.Stamp(DateTimeOffset.Now))
               .Append(" on this host. Every time a tool reports is measured against this.");

        try
        {
            // Games are named the way a person names them, here and in the tools' output alike. The
            // engine's identifier (`lotrrtm`, `projectzomboid.bp`) is not a word anybody says, and this
            // list is read out verbatim on a spoken surface — the model answers "what can I install?"
            // straight from it rather than calling a tool. A name the model hands back to a tool is
            // resolved against both forms, so nothing here has to carry an identifier for it to act.
            var catalog = await _inventory.GetBlueprintCatalogAsync(cancellationToken);
            var labels = catalog.ToDictionary(b => b.Name, b => b.Label, StringComparer.OrdinalIgnoreCase);

            var instances = await _inventory.GetInstancesAsync(cancellationToken);
            builder.Append("\n\nCurrently installed instances:\n");
            if (instances.Count > 0)
            {
                foreach (var (name, game) in instances.OrderBy(kv => kv.Key))
                    builder.Append($"- {name} (game: {GameLabel(game, labels)})\n");
            }
            else
            {
                builder.Append("(none)\n");
            }

            builder.Append("\nInstallable game types (blueprints): ");
            builder.Append(catalog.Count > 0
                ? string.Join(", ", catalog.Select(b => b.Label).OrderBy(l => l, StringComparer.OrdinalIgnoreCase))
                : "(none)");
        }
        catch (Exception ex)
        {
            // The model can still operate (and use list tools) without the injected
            // list, so a lookup failure degrades gracefully rather than aborting.
            _logger.LogWarning(ex, "Failed to inject live lists into system prompt");
        }

        if (styleSegment.Length > 0)
            builder.Append("\n\n").Append(styleSegment);

        return new BuiltPrompt(builder.ToString(), templateHash);
    }

    /// <summary>
    /// The game name for an instance's blueprint. An instance carries its blueprint's file stem, so the
    /// catalog is keyed one <c>.bp</c> suffix shorter; a blueprint the catalog doesn't know (a read that
    /// failed, a game installed from a blueprint since removed) keeps the engine's own word rather than
    /// being dropped — an instance with no game beside it reads as if it had none.
    /// </summary>
    private static string GameLabel(string game, IReadOnlyDictionary<string, string> labels)
    {
        if (string.IsNullOrWhiteSpace(game))
            return game;

        var key = game.EndsWith(".bp", StringComparison.OrdinalIgnoreCase) ? game[..^3] : game;
        return labels.TryGetValue(key, out var label) ? label : game;
    }

    /// <summary>Resolves a segment: the calling leaf's file > the host-wide file > inline config >
    /// lib-owned constant default.</summary>
    /// <summary>
    /// The text for one segment: its file, else the inline config key. There is no third fallback —
    /// a missing segment is a fault, not a turn answered from something the operator cannot see.
    /// The startup check makes this unreachable in practice; it throws rather than returning empty
    /// because a blank segment does not fail, it silently changes what the assistant is.
    /// </summary>
    private string Effective(PromptSegment segment, string? leaf) =>
        _overrides.ReadText(segment.FileName, leaf)
        ?? NullIfBlank(_configuration[segment.ConfigKey])
        ?? throw new AssistantTextUnavailableException(
            $"The prompt segment '{segment.FileName}' is missing or empty, and '{segment.ConfigKey}' " +
            "is not set either. The assistant's prompts live on disk — run deploy/deploy.sh to " +
            "install them.");

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
