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

    // Authorized read (offered only to action-authorized callers — exposes file
    // contents, so gated like the command tier even though it mutates nothing)
    public const string ViewConfigFile = "view_config_file";

    // Staged commands — propose-only (§3.5). The model never executes these; the
    // dispatcher resolves + STAGES them, and a human confirms before they run.
    public const string StartServer = "start_server";
    public const string StopServer = "stop_server";
    public const string RestartServer = "restart_server";
    public const string CreateBackup = "create_backup";
    public const string UpdateServer = "update_server";
    public const string InstallServer = "install_server";
    public const string UninstallServer = "uninstall_server";
    public const string SetConfigValue = "set_config_value";

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

    public static readonly IReadOnlyList<LlmToolDefinition> ReadOnly = new[]
    {
        LlmToolDefinition.Create(GetStatus,
            "Get game server status. With NO instance_name: a single one-shot summary of every " +
            "server (running or stopped) — this is the right tool for \"what's running?\" / \"list " +
            "the servers\". With instance_name: detailed status for that one server.",
            StatusInstanceName),

        LlmToolDefinition.Create(ListBlueprints,
            "List all game types (blueprints) that can be installed."),
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
        LlmToolDefinition.Create(StartServer,
            "Propose starting a stopped server instance. Staged for human confirmation — it does " +
            "not run until a person confirms.", InstanceName),

        LlmToolDefinition.Create(StopServer,
            "Propose stopping a running server instance. Staged for human confirmation — it does " +
            "not run until a person confirms.", InstanceName),

        LlmToolDefinition.Create(RestartServer,
            "Propose restarting (stop then start) a server instance. Staged for human confirmation " +
            "— it does not run until a person confirms.", InstanceName),

        LlmToolDefinition.Create(CreateBackup,
            "Propose creating a backup of a server instance. Staged for human confirmation — it " +
            "does not run until a person confirms.", InstanceName),

        LlmToolDefinition.Create(UpdateServer,
            "Propose updating a server instance to its latest available version. Staged for human " +
            "confirmation — it does not run until a person confirms.", InstanceName),

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
