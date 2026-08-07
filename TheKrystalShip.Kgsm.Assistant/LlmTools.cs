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

    // Network reachability for one instance across two layers: the HOST FIREWALL (the ports KGSM has
    // opened, the firewall backend, its enforcement state — from the kgsm-firewall authority) AND the
    // ROUTER / UPnP forwards (from the watchdog). Read-only and offered to everyone (it reveals no file
    // contents). Each layer is reported honestly and separately — unreachable is "couldn't check", never
    // "nothing open".
    public static readonly Tool GetNetwork = new("get_network");

    // Engine event history, read straight from the engine's event journal (never via kgsm-api —
    // the assistant is a leaf). get_audit_log is the unfiltered "what happened" read;
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

    // Reads ONE specific web page by URL — a leaf capability distinct from `search`: search FINDS
    // pages via provider-summarized hits, this READS a page the model already has (or just found)
    // the URL for (an official docs page, a Steam store page, a raw Dockerfile). Offered iff a
    // fetch adapter is configured (FetchOptions.Available), same §D7 omit-when-disabled rule as
    // `search`; the dispatcher routes it to IWebFetch.
    public static readonly Tool FetchUrl = new("fetch_url");

    // Authorized, autonomous action — offered only to action-authorized callers, but unlike the staged
    // commands below it runs INLINE (no propose→confirm): it touches no user data (it only researches,
    // then test-installs and tears down a disposable probe of its own), so there is nothing for a human
    // to confirm. See LlmTools.AuthorizedActions for why this needs its own tier.
    public static readonly Tool CreateBlueprint = new("create_blueprint");

    // Updates the blueprint draft the user is currently reviewing in the editor — re-validates the
    // supplied full YAML and re-shows it as a fresh draft. Offered ONLY on a turn that carries an open
    // draft (ServerAssistant filters it out otherwise), so the model can act on the current content the
    // SPA sends rather than fabricating a "changed" draft it has no way to actually mutate.
    public static readonly Tool ReviseBlueprint = new("revise_blueprint");

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

    // Propose opening a server's firewall ports for it. The model supplies only the instance name (and
    // optionally include_router); the configured port spec is read deterministically from kgsm's instance
    // info — never passed by the model — so there is no port to guess. Staged for human confirmation
    // like every command; on confirm it opens the host firewall (via the kgsm-firewall authority), and —
    // when include_router is set — also sets up the router/UPnP forward (via the watchdog), honoring the
    // instance's port-forwarding gate.
    public static readonly Tool OpenPorts = new("open_ports");

    // Propose overwriting a text file inside a server's OWN directory with COMPLETE new content —
    // e.g. a game's own config (Palworld's PalWorldSettings.ini), never KGSM's .config.ini (that's
    // set_config_value). Staged for human confirmation like every command; the confirm step shows a
    // preview/diff before it replaces the file. Game-agnostic: it handles whatever text the model
    // composes (a game's own INI/JSON/tuple format), so there is no per-game structure to parse.
    public static readonly Tool WriteFile = new("write_file");

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

    private static readonly LlmToolParameter WritePath = new(
        "path",
        "The file to write, as a path relative to the server's own directory — e.g. " +
        "\"install/Pal/Saved/Config/LinuxServer/PalWorldSettings.ini\" or \"server.properties\". Use " +
        "list_files/read_file first to find and read it.");

    private static readonly LlmToolParameter WriteContent = new(
        "content",
        "The COMPLETE new content for the file. This OVERWRITES the whole file — it is not a patch or " +
        "a diff, so include every existing setting you want to keep, not just the one you're changing.");

    private static readonly LlmToolParameter SearchQuery = new(
        "query",
        "What to look up. Searches the operator's own indexed documentation first, then falls back " +
        "to the public web. For OUTSIDE/background facts that help with the games or servers — e.g. " +
        "a game's latest version, release notes, or what a config option means. NOT for anything " +
        "about this host's own servers (status/config/health) — use the KGSM tools for those.");

    private static readonly LlmToolParameter FetchUrlParam = new(
        "url",
        "The exact web address to read, including scheme — e.g. an official server-setup doc, a " +
        "Steam store page, or a raw Dockerfile URL. Only public http/https pages can be fetched.");

    private static readonly LlmToolParameter BlueprintGame = new(
        "game",
        "The game to add, by its common name (e.g. \"Terraria\", \"Rust\") — not a blueprint slug.");

    private static readonly LlmToolParameter ReviseYaml = new(
        "revised_yaml",
        "The COMPLETE updated blueprint YAML — the whole document (the current draft with the requested " +
        "change applied), not a fragment or a diff. Base it on the open draft's current content shown to " +
        "you this turn; change only what the user asked for and keep every other field as-is.");

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

        LlmToolDefinition.Create(FetchUrl,
            "Fetch and read the full text of ONE specific web page by its exact URL — an official docs " +
            "page, a Steam store page, a GitHub raw file (like a Dockerfile), and so on. Use this when " +
            "you already HAVE a URL (from the user, or from a prior search result) and need its actual " +
            "content. Do NOT use this to find a page in the first place — use `search` for that (it " +
            "looks things up by topic and returns short summaries, not full page content). Only public " +
            "http/https pages can be fetched; large pages may be truncated.",
            FetchUrlParam),
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
    /// Authorized, autonomous actions — offered only to action-authorized callers, like the staged
    /// commands below, but NEVER staged: the dispatcher runs these inline and returns the real outcome,
    /// because they touch nothing of the user's to confirm (today: <see cref="CreateBlueprint"/>, which
    /// only researches and test-installs a disposable probe of its own, torn down before it returns).
    /// A separate tier from <see cref="AuthorizedReadOnly"/> (which mutates nothing at all) and from
    /// <see cref="StagedCommands"/> (which always needs a human confirm) — this is authorized AND
    /// mutating AND confirm-free, a combination none of the existing tiers express honestly.
    /// </summary>
    public static readonly IReadOnlyList<LlmToolDefinition> AuthorizedActions = new[]
    {
        LlmToolDefinition.Create(CreateBlueprint,
            "AUTHORS a game type that is genuinely MISSING from the catalog: researches it online and " +
            "DRAFTS a server config, then shows it to the user in an editor to review and tweak. The " +
            "test-install + verification runs LATER, only when they save the config — so calling this " +
            "does NOT add the game yet. It does its OWN online research, so call it DIRECTLY, in the same " +
            "turn the user asks — do NOT run a separate search/fetch_url or list_blueprints first, and do " +
            "NOT announce that you'll go research it and come back; calling this tool IS how the research " +
            "and drafting start. After it returns, tell the user a draft is ready for them to " +
            "review and save; do NOT claim the game is added or installed. Use this ONLY when the game " +
            "is not in list_blueprints / not offered by install_server — for a game that's already " +
            "installable, propose install_server instead. Not every game can be self-hosted or has a native " +
            "Linux server — relay the outcome honestly either way.",
            BlueprintGame),
    };

    /// <summary>
    /// revise_blueprint — authorized + inline like <see cref="AuthorizedActions"/>, but offered ONLY on a
    /// turn that carries an open draft (its content is injected into that turn), so it is NOT part of
    /// <see cref="All"/>; <c>ServerAssistant.SelectTools</c> appends it when a draft is present. Kept out of
    /// the default catalog so the model can't call it with nothing to revise, and so the unfiltered-catalog
    /// reference identity of <see cref="All"/> holds on an ordinary turn.
    /// </summary>
    public static readonly LlmToolDefinition ReviseBlueprintTool =
        LlmToolDefinition.Create(ReviseBlueprint,
            "UPDATES the blueprint draft the user is CURRENTLY reviewing in the editor. Use this whenever " +
            "they ask to change, populate, fix, or add anything to the open draft — a metadata field " +
            "(min/recommended RAM, max players, disk), a port, the launch args, anything. The draft's " +
            "current YAML is given to you in THIS turn's context: take it, apply ONLY the requested change " +
            "(research any values first with search if you need them), and pass the COMPLETE updated YAML " +
            "as revised_yaml. It re-validates and shows the user the updated draft to review and save — " +
            "nothing is installed. You have NO other way to change the draft: NEVER say you updated, " +
            "populated, or added to it unless you called this tool and it succeeded.",
            ReviseYaml);

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
            "Propose opening this server's firewall ports so players can reach it. You do NOT pass the " +
            "ports — they are read automatically from the server's configured (blueprint) ports, so just " +
            "give the instance name. Staged for human confirmation; it does not run until a person " +
            "confirms. By default it opens the host's own firewall only. To ALSO make the server reachable " +
            "from the internet, set include_router to true — that additionally sets up the router/UPnP " +
            "forward for the same ports (only effective if the server has port-forwarding enabled; " +
            "otherwise the router leg is skipped). If the server has no ports configured, the tool " +
            "returns an honest error and nothing is staged — relay that to the user.",
            InstanceName, IncludeRouter),

        LlmToolDefinition.Create(WriteFile,
            "Propose OVERWRITING a text file inside a game server's OWN directory — e.g. its actual " +
            "game config file (Palworld's PalWorldSettings.ini, server.properties, a mod's settings " +
            "file) — with content you provide. This is for the GAME's own config; for KGSM's own " +
            "settings (ports, launch arguments, auto-update) use set_config_value instead. Give the " +
            "COMPLETE new file content (this replaces the whole file, not a patch) — only propose an " +
            "overwrite of a file you've read in full with read_file (or a brand-new file), and preserve " +
            "every setting you're not changing. Staged for human confirmation against a preview; it " +
            "does not run until a person confirms, and a running server picks up the change on its next " +
            "restart. Never claim to have written the file yourself.",
            InstanceName, WritePath, WriteContent),
    };

    /// <summary>All tools, offered to callers authorized for actions.</summary>
    public static readonly IReadOnlyList<LlmToolDefinition> All =
        ReadOnly.Concat(AuthorizedReadOnly).Concat(StagedCommands).Concat(AuthorizedActions).ToArray();

    /// <summary>Tools of authorized-only reads; refused for unauthorized callers, but not capped.</summary>
    public static readonly IReadOnlySet<Tool> AuthorizedReadTools =
        AuthorizedReadOnly.Select(t => t.Tool).ToHashSet();

    /// <summary>
    /// Tools of the propose-only commands; offered only to authorized callers, staged
    /// (never executed inline), and counted against the per-message staging cap.
    /// </summary>
    public static readonly IReadOnlySet<Tool> StagedCommandTools =
        StagedCommands.Select(t => t.Tool).ToHashSet();

    /// <summary>Tools of the authorized, autonomous (confirm-free) actions; refused for unauthorized
    /// callers, run inline (never staged) — see <see cref="AuthorizedActions"/>. Includes the
    /// draft-only <see cref="ReviseBlueprint"/> (offered conditionally, but the same authorized+inline
    /// tier as create_blueprint when it IS offered).</summary>
    public static readonly IReadOnlySet<Tool> AuthorizedActionTools =
        AuthorizedActions.Select(t => t.Tool).Append(ReviseBlueprint).ToHashSet();

    /// <summary>All valid tool names, for validating client requests against the server catalog.</summary>
    public static IReadOnlySet<Tool> AllToolNames => All.Select(t => t.Tool).ToHashSet();

    /// <summary>
    /// Every tool this assistant can EVER dispatch, including the ones offered only in context —
    /// today just <see cref="ReviseBlueprint"/>, appended by <c>ServerAssistant.SelectTools</c> on a
    /// turn that carries an open draft. Distinct from <see cref="AllToolNames"/>, which is the
    /// ordinary-turn OFFER: asking "did this tool ever exist?" of the offer set would report a
    /// conditionally-offered tool as one the model invented.
    /// </summary>
    public static IReadOnlySet<Tool> EveryToolName =>
        All.Select(t => t.Tool).Append(ReviseBlueprint).ToHashSet();

    public static bool IsStagedCommand(Tool tool) => StagedCommandTools.Contains(tool);

    public static bool IsAuthorizedRead(Tool tool) => AuthorizedReadTools.Contains(tool);

    public static bool IsAuthorizedAction(Tool tool) => AuthorizedActionTools.Contains(tool);
}
