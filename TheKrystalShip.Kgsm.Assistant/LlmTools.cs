using TheKrystalShip.Kgsm.Assistant.Audit;
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
    public static readonly Tool GetStatus = new("get_status");
    public static readonly Tool ListBlueprints = new("list_blueprints");
    public static readonly Tool RunHealthCheck = new("run_health_check");

    // Live per-server resource usage (CPU/memory/network/disk-io/pids) — a snapshot of current
    // measured values from the metrics monitor. Status-sensitive like health, so it's a read-only
    // tool offered to everyone, not a file-content read.
    public static readonly Tool GetPerformance = new("get_performance");

    // Host-firewall picture for one instance — the ports KGSM has opened for it plus the firewall
    // backend and its enforcement state, read from the kgsm-firewall authority. Read-only and offered
    // to everyone (it reveals no file contents). Covers the HOST FIREWALL only: router/UPnP forwarding
    // is not observable from the host and is never reported (honesty rule).
    public static readonly Tool GetNetwork = new("get_network");

    // Engine event history, read straight from the kgsm-monitor's event store (never via kgsm-api —
    // the assistant is a leaf, plan §9). get_audit_log is the unfiltered "what happened" read;
    // get_change_timeline shares the same source but narrows to the state-changing subset and frames
    // it as "what changed" (see Audit.AuditReport.ChangeEventTypes for the exact set).
    public static readonly Tool GetAuditLog = new("get_audit_log");
    public static readonly Tool GetChangeTimeline = new("get_change_timeline");

    // The capstone aggregator (toolbox-plan §3.4/§7·Q1): a DETERMINISTIC composition of the event
    // timeline + a metrics window + a health snapshot for ONE instance, run through a fixed rules
    // table of known KGSM failure signatures. No nested model call — the model only narrates the
    // finding this tool already computed (RootCause.RootCauseAggregator). Per-instance only
    // (unlike get_audit_log/get_change_timeline, instance_name is REQUIRED — root cause needs a
    // single subject).
    public static readonly Tool TraceRootCause = new("trace_root_cause");

    // The unified knowledge-search tool (§3.4): the operator's indexed docs first, the public web as
    // a fallback. Replaces the former model-facing `web_search` — IWebSearch is now an internal
    // capability the `search` aggregator composes, not a tool the model picks directly. Offered iff a
    // source backs it (SearchOptions.Available); the dispatcher routes it to ISearch.
    public static readonly Tool Search = new("search");

    // Authorized read (offered only to action-authorized callers — exposes file
    // contents, so gated like the command tier even though it mutates nothing).
    // read_file replaces the old single-purpose view_config_file: it reads any text file
    // inside a server's own directory (config, logs, server.properties, mod settings …),
    // and list_files lets the model discover what's there first.
    public static readonly Tool ReadFile = new("read_file");
    public static readonly Tool ListFiles = new("list_files");

    // Staged commands — propose-only (§3.5). The model never executes these; the
    // dispatcher resolves + STAGES them, and a human confirms before they run.

    // The lifecycle actions (start/stop/restart/update/backup) are ONE tool with a
    // `verb` parameter (§4.1) — collapsed from five near-identical tools so the small
    // local model faces fewer, less-overlapping choices (§3.2). install/uninstall stay
    // separate (different params + confirm tiers); set_config carries a key/value.
    public static readonly Tool ServerCommand = new("server_command");
    public static readonly Tool InstallServer = new("install_server");
    public static readonly Tool UninstallServer = new("uninstall_server");
    public static readonly Tool SetConfigValue = new("set_config_value");

    // Propose opening HOST-FIREWALL ports for an instance (via the kgsm-firewall authority). Staged for
    // human confirmation like every command; on confirm it opens the host firewall only — it does NOT
    // configure router/UPnP port forwarding (no such control surface exists from the host).
    public static readonly Tool OpenPorts = new("open_ports");

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

    private static readonly LlmToolParameter PerformanceRange = new(
        "range",
        "Optional. OMIT for a live snapshot of current usage. Provide a time window to get the TREND " +
        "over that period instead (a chart of how CPU/memory changed) — use this for \"how has X been " +
        "doing over the last hour/day?\", \"is X's memory climbing?\", \"CPU trend for X\".",
        Required: false,
        AllowedValues: new[] { "1h", "24h", "7d", "30d" });

    private static readonly LlmToolParameter AuditInstanceName = new(
        "instance_name",
        "Optional. Scope the events to this one server. OMIT to get events across every server on " +
        "this host — do that for questions like \"what happened recently?\" / \"any errors lately?\".",
        Required: false);

    private static readonly LlmToolParameter AuditWindowParam = new(
        "window",
        "Optional. How far back to look. Defaults to 24h if omitted.",
        Required: false,
        AllowedValues: AuditWindow.AllowedValues);

    private static readonly LlmToolParameter ChangeTimelineRange = new(
        "range",
        "Optional. How far back to look. Defaults to 7d if omitted.",
        Required: false,
        AllowedValues: AuditWindow.AllowedValues);

    private static readonly LlmToolParameter RootCauseRange = new(
        "range",
        "Optional. How far back to look for evidence. Defaults to 24h if omitted.",
        Required: false,
        AllowedValues: AuditWindow.AllowedValues);

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

    private static readonly LlmToolParameter OpenPortsSpec = new(
        "ports",
        "The port(s) to open — e.g. \"34197/udp\", \"27015/tcp\", or a range " +
        "\"27015:27020/udp\". Separate multiple with commas. Include the protocol (tcp or udp); if you " +
        "omit it, both are opened.");

    private static readonly LlmToolParameter IncludeRouter = new(
        "include_router",
        "Optional. Set to true to ALSO set up the router/UPnP port forward for these ports (so the server " +
        "is reachable from the internet), not just the host firewall. Use it when the user wants the server " +
        "reachable from outside their network. The router leg only takes effect if the server has " +
        "port-forwarding enabled; otherwise it's skipped. Defaults to false (host firewall only).",
        Required: false);

    private static readonly LlmToolParameter ReadPath = new(
        "path",
        "Optional. The file to read, as a path relative to the server's own directory — e.g. " +
        "\"server.properties\" or \"logs/latest.log\". OMIT it to read the server's main " +
        "configuration (its .config.ini). Use list_files first if you don't know the file name.",
        Required: false);

    private static readonly LlmToolParameter ListSubdir = new(
        "subdir",
        "Optional. A subdirectory to list, relative to the server's own directory — e.g. \"logs\" " +
        "or \"install\". OMIT it to list the server's top-level directory.",
        Required: false);

    private static readonly LlmToolParameter SearchQuery = new(
        "query",
        "What to look up. Searches the operator's own indexed documentation first, then falls back " +
        "to the public web. For OUTSIDE/background facts that help with the games or servers — e.g. " +
        "a game's latest version, release notes, or what a config option means. NOT for anything " +
        "about this host's own servers (status/config/health) — use the KGSM tools for those.");

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

        LlmToolDefinition.Create(GetPerformance,
            "Get ONE server's resource usage — CPU (as % of one core), memory, network and disk-I/O " +
            "throughput, process count. Without a range it's a LIVE snapshot of current values (\"how " +
            "much is X using?\", \"is X hammering the CPU/RAM?\"); with a range it's the TREND over that " +
            "window as a chart (\"how has X been doing over the last hour/day?\", \"is X's memory " +
            "climbing?\"). A stopped server has no live snapshot but may still have recent history.",
            InstanceName, PerformanceRange),

        LlmToolDefinition.Create(GetNetwork,
            "Report the network reachability of ONE server across two layers: the HOST FIREWALL (the ports " +
            "KGSM has opened, the firewall backend, and whether it's enforcing) AND the ROUTER / UPnP port " +
            "forwards it has on the local router. Use this for \"what ports are open for X?\", \"is X's port " +
            "allowed through the firewall?\", \"is X forwarded on the router?\", or \"is X reachable from the " +
            "internet?\". Both layers are reported honestly and separately — an unreachable firewall or router " +
            "is 'couldn't check', never 'nothing open'.",
            InstanceName),

        LlmToolDefinition.Create(GetAuditLog,
            "Get recent operational events for a server (or every server) — starts, stops, crashes, " +
            "installs, updates, backups, port changes, player activity — most recent first, read " +
            "straight from KGSM's own event log. Use this for \"what happened to X?\", \"has X crashed " +
            "recently?\", or \"what's been going on?\". For a narrower \"what CHANGED\" question " +
            "(installs/updates/version/backups/port changes only, no routine starts/stops or player " +
            "activity), use get_change_timeline instead.",
            AuditInstanceName, AuditWindowParam),

        LlmToolDefinition.Create(GetChangeTimeline,
            "Get a timeline of durable CHANGES to a server (or every server) — installs, uninstalls, " +
            "updates, version changes, backups, and port changes — most recent first. Deliberately " +
            "excludes routine starts/stops and player join/leave (use get_audit_log for the full " +
            "event feed). Use this for \"what changed on X?\", \"when was X last updated?\", or " +
            "\"has anything changed recently?\".",
            AuditInstanceName, ChangeTimelineRange),

        LlmToolDefinition.Create(TraceRootCause,
            "Investigate WHY one server crashed, won't stay up, or has been misbehaving recently — " +
            "an incident/history question, not a right-now check (use run_health_check for \"is X " +
            "healthy now?\"). Automatically pulls together its event history, resource usage, and " +
            "health checks and matches them against known failure patterns (port conflicts, " +
            "update-triggered crash loops, disk-full failures, event-log/live-state mismatches), " +
            "returning a ranked explanation with the evidence behind it. Use this for \"why did X " +
            "crash?\" or \"why did X stop working?\" instead of calling get_audit_log/get_performance " +
            "separately and reasoning it out yourself. If no known pattern matches, it honestly " +
            "reports a correlation instead of guessing a cause.",
            InstanceName, RootCauseRange),

        LlmToolDefinition.Create(Search,
            "Look something up in the knowledge base: the operator's indexed documentation first, then " +
            "the public web as a fallback. Returns short passages, each with its source (a doc path or " +
            "a URL). Use it ONLY for outside facts that help with the games/servers — a game's latest " +
            "version, patch notes, or what a setting does. Results may be external and out of date, so " +
            "cite the sources and don't state them as certain. Do NOT use this for anything about this " +
            "host's own servers (status, config, health) — the KGSM tools are authoritative for those.",
            SearchQuery),
    };

    /// <summary>
    /// Reads that expose file contents — offered only to action-authorized callers.
    /// They mutate nothing (no per-message cap, no staging), but reading a server's
    /// files reveals more than the read-only tier, so the gate refuses them for
    /// unauthorized callers as defense-in-depth. (V1 owner-decision: conservative;
    /// can be relaxed into <see cref="ReadOnly"/> later — tightening after exposure
    /// is the harder direction.)
    /// </summary>
    public static readonly IReadOnlyList<LlmToolDefinition> AuthorizedReadOnly = new[]
    {
        LlmToolDefinition.Create(ReadFile,
            "Read a text file from inside a game server's own directory — its configuration, logs, " +
            "server.properties, mod settings, and so on. Read-only and confined to that server's " +
            "folder. Give the file's path relative to the server's directory (use list_files first " +
            "if you don't know it), or OMIT the path to read the server's main configuration (its " +
            ".config.ini). Large files are truncated and binary files aren't shown. To CHANGE a " +
            "config setting, propose it with set_config_value instead.",
            InstanceName, ReadPath),

        LlmToolDefinition.Create(ListFiles,
            "List the files and folders inside a game server's own directory, so you can find a file " +
            "to read with read_file. Optionally pass a subdirectory (e.g. \"logs\") to look inside it; " +
            "omit it for the server's top level.",
            InstanceName, ListSubdir),
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

        LlmToolDefinition.Create(OpenPorts,
            "Propose opening ports for a server so players can reach it — e.g. its game port. Staged for " +
            "human confirmation; it does not run until a person confirms. By default it opens the host's own " +
            "firewall only. To ALSO make the server reachable from the internet, set include_router to true — " +
            "that additionally sets up the router/UPnP forward for the same ports (only effective if the " +
            "server has port-forwarding enabled; otherwise the router leg is skipped).",
            InstanceName, OpenPortsSpec, IncludeRouter),
    };

    /// <summary>All tools, offered to callers authorized for actions.</summary>
    public static readonly IReadOnlyList<LlmToolDefinition> All =
        ReadOnly.Concat(AuthorizedReadOnly).Concat(StagedCommands).ToArray();

    /// <summary>Tools of authorized-only reads; refused for unauthorized callers, but not capped.</summary>
    public static readonly IReadOnlySet<Tool> AuthorizedReadTools =
        AuthorizedReadOnly.Select(t => t.Tool).ToHashSet();

    /// <summary>
    /// Tools of the propose-only commands; offered only to authorized callers, staged
    /// (never executed inline), and counted against the per-message staging cap.
    /// </summary>
    public static readonly IReadOnlySet<Tool> StagedCommandTools =
        StagedCommands.Select(t => t.Tool).ToHashSet();

    /// <summary>All valid tool names, for validating client requests against the server catalog.</summary>
    public static IReadOnlySet<Tool> AllToolNames => All.Select(t => t.Tool).ToHashSet();

    public static bool IsStagedCommand(Tool tool) => StagedCommandTools.Contains(tool);

    public static bool IsAuthorizedRead(Tool tool) => AuthorizedReadTools.Contains(tool);
}
