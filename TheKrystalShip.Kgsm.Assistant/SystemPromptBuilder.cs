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
        string? leaf = null)
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

        // Hash ONLY the template — not the live lists appended below — so the recorded id moves when
        // the prompt is tuned, not when a server is installed mid-session.
        var templateHash = PromptHash.Short(template);

        var builder = new StringBuilder(template);

        try
        {
            var instances = await _inventory.GetInstancesAsync(cancellationToken);
            builder.Append("\n\nCurrently installed instances:\n");
            if (instances.Count > 0)
            {
                foreach (var (name, game) in instances.OrderBy(kv => kv.Key))
                    builder.Append($"- {name} (game: {game})\n");
            }
            else
            {
                builder.Append("(none)\n");
            }

            var blueprints = await _inventory.GetBlueprintNamesAsync(cancellationToken);
            builder.Append("\nInstallable game types (blueprints): ");
            builder.Append(blueprints.Count > 0
                ? string.Join(", ", blueprints.OrderBy(k => k))
                : "(none)");
        }
        catch (Exception ex)
        {
            // The model can still operate (and use list tools) without the injected
            // list, so a lookup failure degrades gracefully rather than aborting.
            _logger.LogWarning(ex, "Failed to inject live lists into system prompt");
        }

        return new BuiltPrompt(builder.ToString(), templateHash);
    }

    /// <summary>Resolves a segment: the calling leaf's file > the host-wide file > inline config >
    /// lib-owned constant default.</summary>
    private string Effective(PromptSegment segment, string? leaf) =>
        _overrides.ReadText(segment.FileName, leaf)
        ?? NullIfBlank(_configuration[segment.ConfigKey])
        ?? segment.Default;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
