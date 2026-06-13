using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant;

/// <summary>
/// The whitelist of tools the model may call. Each entry maps 1:1 onto a handler
/// in <see cref="ToolDispatcher"/>.
///
/// Tools fall into three tiers:
///  - <see cref="ReadOnly"/>: open to everyone.
///  - <see cref="Mutating"/>: offered only to authorized callers; gated in
///    <see cref="ServerAssistant"/> (authorization + a per-message action cap).
///  - <see cref="Destructive"/>: offered only to authorized callers, but NEVER
///    executed inline — the dispatcher resolves and STAGES them, and execution
///    happens only after an explicit human confirmation handled by the host. These
///    are the data-losing / heavy ops.
/// </summary>
public static class LlmTools
{
    // Read-only (offered to everyone)
    public const string GetStatus = "get_status";
    public const string ListBlueprints = "list_blueprints";

    // Authorized read (offered only to action-authorized callers — exposes file
    // contents, so gated like the action tier even though it mutates nothing)
    public const string ViewConfigFile = "view_config_file";

    // Mutating
    public const string StartServer = "start_server";
    public const string StopServer = "stop_server";
    public const string RestartServer = "restart_server";
    public const string CreateBackup = "create_backup";
    public const string UpdateServer = "update_server";

    // Destructive (staged behind human confirmation)
    public const string InstallServer = "install_server";
    public const string UninstallServer = "uninstall_server";

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
            "redacted. Use this to inspect or help diagnose a server's settings.",
            InstanceName),
    };

    public static readonly IReadOnlyList<LlmToolDefinition> Mutating = new[]
    {
        LlmToolDefinition.Create(StartServer,
            "Start a stopped server instance.", InstanceName),

        LlmToolDefinition.Create(StopServer,
            "Stop a running server instance.", InstanceName),

        LlmToolDefinition.Create(RestartServer,
            "Restart (stop then start) a server instance.", InstanceName),

        LlmToolDefinition.Create(CreateBackup,
            "Create a backup of a server instance.", InstanceName),

        LlmToolDefinition.Create(UpdateServer,
            "Update a server instance to its latest available version.", InstanceName),
    };

    public static readonly IReadOnlyList<LlmToolDefinition> Destructive = new[]
    {
        LlmToolDefinition.Create(InstallServer,
            "Install a NEW game server from a blueprint. Heavy and slow; requires human " +
            "confirmation before it runs.", BlueprintName, OptionalInstanceName),

        LlmToolDefinition.Create(UninstallServer,
            "PERMANENTLY delete a server instance and all its data. Irreversible; requires " +
            "human confirmation before it runs.", InstanceName),
    };

    /// <summary>All tools, offered to callers authorized for actions.</summary>
    public static readonly IReadOnlyList<LlmToolDefinition> All =
        ReadOnly.Concat(AuthorizedReadOnly).Concat(Mutating).Concat(Destructive).ToArray();

    /// <summary>Names of authorized-only reads; refused for unauthorized callers, but not capped.</summary>
    public static readonly IReadOnlySet<string> AuthorizedReadNames =
        AuthorizedReadOnly.Select(t => t.Name).ToHashSet();

    /// <summary>Names of tools that change server state; gated and counted against the cap.</summary>
    public static readonly IReadOnlySet<string> MutatingNames =
        Mutating.Select(t => t.Name).ToHashSet();

    /// <summary>Names of data-losing / heavy tools that are staged for human confirmation.</summary>
    public static readonly IReadOnlySet<string> DestructiveNames =
        Destructive.Select(t => t.Name).ToHashSet();

    public static bool IsMutating(string toolName) => MutatingNames.Contains(toolName);

    public static bool IsDestructive(string toolName) => DestructiveNames.Contains(toolName);

    public static bool IsAuthorizedRead(string toolName) => AuthorizedReadNames.Contains(toolName);
}
