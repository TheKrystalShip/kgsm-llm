using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant;

/// <summary>
/// The whitelist of tools the model may call. Each entry maps 1:1 onto a handler
/// in <see cref="ToolDispatcher"/>.
///
/// Tools fall into three tiers:
///  - <see cref="ReadOnly"/>: open to everyone.
///  - <see cref="AuthorizedReadOnly"/>: offered only to action-authorized callers
///    (exposes file contents), but mutates nothing — runs inline, uncapped.
///  - <see cref="StagedCommands"/>: every server command (§3.5). Offered only to
///    authorized callers and NEVER executed inline — the dispatcher resolves and
///    STAGES them, and execution happens only after an explicit human confirmation
///    handled by the host. The model only ever PROPOSES a command.
/// </summary>
public static class LlmTools
{
    // Read-only (offered to everyone)
    public const string GetStatus = "get_status";
    public const string ListBlueprints = "list_blueprints";
    public const string RunHealthCheck = "run_health_check";
    public const string WebSearch = "web_search";

    // Authorized read (offered only to action-authorized callers — exposes file
    // contents, so gated like the command tier even though it mutates nothing)
    public const string ViewConfigFile = "view_config_file";

    // Staged commands — propose-only (§3.5). The model never executes these; the
    // dispatcher resolves + STAGES them, and a human confirms before they run.

    // The lifecycle actions (start/stop/restart/update/backup) are ONE tool with a
    // `verb` parameter (§4.1) — collapsed from five near-identical tools so the small
    // local model faces fewer, less-overlapping choices (§3.2). install/uninstall stay
    // separate (different params + confirm tiers); set_config carries a key/value.
    public const string ServerCommand = "server_command";
    public const string InstallServer = "install_server";
    public const string UninstallServer = "uninstall_server";
    public const string SetConfigValue = "set_config_value";

    /// <summary>
    /// The verbs <see cref="ServerCommand"/> accepts, in display order. Single source of
    /// truth for both the tool's <c>enum</c> schema and the dispatcher's verb→kind routing
    /// (<see cref="ServerCommandKind"/>) — so the catalog and the dispatcher cannot drift.
    /// </summary>
    public static readonly IReadOnlyList<string> ServerCommandVerbs =
        new[] { "start", "stop", "restart", "update", "backup" };

    /// <summary>
    /// Maps a <see cref="ServerCommand"/> verb token to its <see cref="ConfirmationKind"/>;
    /// returns null for an unknown/missing verb (the dispatcher then refuses the call). The
    /// token differs from <see cref="ConfirmationKinds.Verb"/> ("backup" vs the label "back up"),
    /// which is why this is its own map.
    /// </summary>
    public static ConfirmationKind? ServerCommandKind(string? verb) =>
        verb?.Trim().ToLowerInvariant() switch
        {
            "start" => ConfirmationKind.Start,
            "stop" => ConfirmationKind.Stop,
            "restart" => ConfirmationKind.Restart,
            "update" => ConfirmationKind.Update,
            "backup" => ConfirmationKind.Backup,
            _ => null,
        };

    private static readonly LlmToolParameter InstanceName = new(
        "instance_name", "The exact name of the server instance.");

    private static readonly LlmToolParameter StatusInstanceName = new(
        "instance_name",
        "The server to report on in detail. OMIT this to get a one-shot summary of EVERY server " +
        "at once — always do that for questions like \"which servers are running?\" instead of " +
        "checking servers one at a time.",
        Required: false);

    private static readonly LlmToolParameter BlueprintName = new(
        "blueprint_name", "The game type (blueprint) to install, from the installable list.");

    private static readonly LlmToolParameter OptionalInstanceName = new(
        "instance_name", "Optional custom name for the new instance. Omit to let the system name it.",
        Required: false);

    private static readonly LlmToolParameter ConfigKey = new(
        "config_key",
        "The configuration key to set, e.g. \"auto_update\", \"executable_arguments\", or " +
        "\"stop_command_timeout_seconds\".");

    private static readonly LlmToolParameter ConfigValue = new(
        "config_value",
        "The new value for the key. Pass an empty string to clear the setting.");

    private static readonly LlmToolParameter ServerVerb = new(
        "verb",
        "Which lifecycle action to take on the server: start, stop, restart, update, or backup.",
        AllowedValues: ServerCommandVerbs);

    private static readonly LlmToolParameter SearchQuery = new(
        "query",
        "What to look up on the public web. For OUTSIDE facts only — e.g. a game's latest " +
        "version, release notes, or what a config option means. NOT for anything about this " +
        "host's own servers (status/config/health) — use the KGSM tools for those.");

    public static readonly IReadOnlyList<LlmToolDefinition> ReadOnly = new[]
    {
        LlmToolDefinition.Create(GetStatus,
            "Get game server status. With NO instance_name: a single one-shot summary of every " +
            "server (running or stopped) — this is the right tool for \"what's running?\" / \"list " +
            "the servers\". With instance_name: detailed status for that one server.",
            StatusInstanceName),

        LlmToolDefinition.Create(ListBlueprints,
            "List all game types (blueprints) that can be installed."),

        LlmToolDefinition.Create(RunHealthCheck,
            "Run a quick health check on ONE server and get a ranked summary. Checks whether it's " +
            "running, scans its recent logs for errors, reports whether an update is available, and " +
            "checks host disk space. Use this for \"is X healthy / OK?\" or \"what's wrong with X?\" " +
            "instead of fetching status, logs and disk separately.",
            InstanceName),

        LlmToolDefinition.Create(WebSearch,
            "Search the public web and get back short extracted snippets, each with its source URL. " +
            "Use it ONLY for outside facts that help with the games/servers — a game's latest " +
            "version, patch notes, or what a setting does. The results are external and may be out " +
            "of date, so cite the source URLs and don't state them as certain. Do NOT use this for " +
            "anything about this host's own servers (status, config, health) — the KGSM tools are " +
            "authoritative for those.",
            SearchQuery),
    };

    /// <summary>
    /// Reads that expose file contents — offered only to action-authorized callers.
    /// They mutate nothing (no per-message cap, no staging), but reading a config
    /// file reveals more than the read-only tier, so the gate refuses them for
    /// unauthorized callers as defense-in-depth. (V1 owner-decision: conservative;
    /// can be relaxed into <see cref="ReadOnly"/> later — tightening after exposure
    /// is the harder direction.)
    /// </summary>
    public static readonly IReadOnlyList<LlmToolDefinition> AuthorizedReadOnly = new[]
    {
        LlmToolDefinition.Create(ViewConfigFile,
            "View a game server's main configuration file (its .config.ini), with secrets " +
            "redacted. Read-only — use it to inspect or help diagnose a server's settings; to " +
            "CHANGE a setting, propose it with set_config_value instead.",
            InstanceName),
    };

    /// <summary>
    /// Every server command — all propose-only (§3.5). The dispatcher STAGES each for
    /// human confirmation; none runs in the agent loop. Descriptions say so explicitly,
    /// so the model narrates "I've proposed…", never "I've done it."
    /// </summary>
    public static readonly IReadOnlyList<LlmToolDefinition> StagedCommands = new[]
    {
        LlmToolDefinition.Create(ServerCommand,
            "Propose a lifecycle action on an EXISTING server instance — choose it with the 'verb' " +
            "parameter: start (a stopped server), stop (a running one), restart (stop then start), " +
            "update (to its latest version), or backup (create a backup). Staged for human " +
            "confirmation — it does not run until a person confirms.",
            InstanceName, ServerVerb),

        LlmToolDefinition.Create(InstallServer,
            "Propose installing a NEW game server from a blueprint. Heavy and slow; staged for " +
            "human confirmation — it does not run until a person confirms.",
            BlueprintName, OptionalInstanceName),

        LlmToolDefinition.Create(UninstallServer,
            "Propose PERMANENTLY deleting a server instance and all its data. Irreversible; staged " +
            "for human confirmation — it does not run until a person confirms.", InstanceName),

        LlmToolDefinition.Create(SetConfigValue,
            "Propose setting one key=value in a server's configuration file (its .config.ini), e.g. " +
            "auto_update, executable_arguments, or a timeout setting. Staged for human confirmation — " +
            "it does not run until a person confirms. kgsm refuses identity/structural keys, path keys " +
            "(*_dir/*_file), ports, and the integration toggles (enable_firewall_management/_port_" +
            "forwarding/_command_shortcuts) — those have dedicated flows — so proposing one of those is " +
            "rejected when confirmed; tell the user rather than retrying.",
            InstanceName, ConfigKey, ConfigValue),
    };

    /// <summary>All tools, offered to callers authorized for actions.</summary>
    public static readonly IReadOnlyList<LlmToolDefinition> All =
        ReadOnly.Concat(AuthorizedReadOnly).Concat(StagedCommands).ToArray();

    /// <summary>Names of authorized-only reads; refused for unauthorized callers, but not capped.</summary>
    public static readonly IReadOnlySet<string> AuthorizedReadNames =
        AuthorizedReadOnly.Select(t => t.Name).ToHashSet();

    /// <summary>
    /// Names of the propose-only commands; offered only to authorized callers, staged
    /// (never executed inline), and counted against the per-message staging cap.
    /// </summary>
    public static readonly IReadOnlySet<string> StagedCommandNames =
        StagedCommands.Select(t => t.Name).ToHashSet();

    public static bool IsStagedCommand(string toolName) => StagedCommandNames.Contains(toolName);

    public static bool IsAuthorizedRead(string toolName) => AuthorizedReadNames.Contains(toolName);
}
