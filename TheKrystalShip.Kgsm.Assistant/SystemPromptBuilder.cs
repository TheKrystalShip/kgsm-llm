using System.Text;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using TheKrystalShip.Kgsm.Assistant.Ports;

namespace TheKrystalShip.Kgsm.Assistant;

/// <inheritdoc />
public class SystemPromptBuilder : ISystemPromptBuilder
{
    private readonly IServerInventory _inventory;
    private readonly ILogger<SystemPromptBuilder> _logger;
    private readonly IConfiguration _configuration;

    public SystemPromptBuilder(IServerInventory inventory, ILogger<SystemPromptBuilder> logger, IConfiguration configuration)
    {
        _inventory = inventory;
        _logger = logger;
        _configuration = configuration;
    }

    public async Task<string> BuildAsync(bool canPerformActions, CancellationToken cancellationToken = default)
    {
        // Config overrides the lib-owned canonical text; absent keys fall back to the
        // shared defaults so a new host needs no Llm:* config to match every other host.
        var preamble = _configuration["Llm:Preamble"] ?? KgsmAssistantPrompts.Preamble;
        var builder = new StringBuilder(preamble);
        builder.Append(canPerformActions
            ? _configuration["Llm:ActionsAllowed"] ?? KgsmAssistantPrompts.ActionsAllowed
            : _configuration["Llm:ActionsDenied"] ?? KgsmAssistantPrompts.ActionsDenied);

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

        return builder.ToString();
    }
}
