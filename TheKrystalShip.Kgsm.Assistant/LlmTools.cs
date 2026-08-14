using TheKrystalShip.Kgsm.Assistant.Audit;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant;

/// <summary>
/// The whitelist of tools the model may call. Each entry maps 1:1 onto a handler
/// in <see cref="ToolDispatcher"/>.
///
/// <para>Two rules generate the catalog:</para>
/// <list type="bullet">
/// <item><b>Reads and mutations never share a tool.</b> A tool's authorization tier is decided
/// before it is offered, so a tool whose tier depended on an argument could not be offered
/// honestly. Reads are <c>*_info</c>; mutations are <c>*_command</c>.</item>
/// <item><b>A tool owns a noun; an enum selects the operation.</b> The enum reaches the model as a
/// JSON-schema <c>enum</c>, which makes it a whitelist — a small local model cannot invent an
/// operation the way it can invent a free-form command string — and it keeps
/// <c>(tool, verb)</c> a static map onto <see cref="ConfirmationKind"/>.</item>
/// </list>
///
/// Tools fall into four tiers:
/// <list type="bullet">
/// <item><see cref="ReadOnly"/>: open to everyone.</item>
/// <item><see cref="AuthorizedReadOnly"/>: offered only to action-authorized callers
/// (exposes file and console contents), but mutates nothing — runs inline, uncapped.</item>
/// <item><see cref="AuthorizedActions"/>: authorized, mutating, and confirm-free — run inline
/// because they touch nothing of the user's to confirm.</item>
/// <item><see cref="StagedCommands"/>: every server command. Offered only to authorized callers
/// and NEVER executed inline — the dispatcher resolves and STAGES them, and execution happens only
/// after an explicit human confirmation handled by the host. The model only ever PROPOSES.</item>
/// </list>
/// </summary>
public static class LlmTools
{
    // Read-only (offered to everyone)

    // The per-instance read. One tool over every fact about a server, selected by `aspect`, rather
    // than a tool per fact: fewer, less-overlapping choices is what a small local model routes well.
    public static readonly Tool ServerInfo = new("server_info");

    // The host's own vitals and port usage — the machine, not any one server.
    public static readonly Tool HostInfo = new("host_info");

    // The catalog read: every installable game type, or one game type's detail.
    public static readonly Tool BlueprintInfo = new("blueprint_info");

    // Engine event history, read straight from the engine's event journal (never via kgsm-api —
    // the assistant is a leaf). `scope` picks the unfiltered "what happened" feed or the narrower
    // state-changing "what changed" subset; both read the same journal.
    public static readonly Tool Events = new("events");

    public static readonly Tool RunHealthCheck = new("run_health_check");

    // Live per-server resource usage (CPU/memory/network/disk-io/pids) — a snapshot of current
    // measured values from the metrics monitor. Status-sensitive like health, so it's a read-only
    // tool offered to everyone, not a file-content read.
    public static readonly Tool GetPerformance = new("get_performance");

    // Network reachability for one instance across two layers: the HOST FIREWALL (the ports KGSM has
    // opened, the firewall backend, its enforcement state — from the kgsm-firewall authority) AND the
    // ROUTER / UPnP forwards (from the watchdog). Read-only and offered to everyone (it reveals no file
    // contents). Each layer is reported honestly and separately — unreachable is "couldn't check", never
    // "nothing open". This tool owns the port question; server_info deliberately has no ports aspect,
    // so the model never has to choose between two tools that both look like they answer it.
    public static readonly Tool GetNetwork = new("get_network");

    // The capstone aggregator: a DETERMINISTIC composition of the event timeline + a metrics window +
    // a health snapshot for ONE instance, run through a fixed rules table of known KGSM failure
    // signatures. No nested model call — the model only narrates the finding this tool already
    // computed (RootCause.RootCauseAggregator). Per-instance only (unlike events, instance_name is
    // REQUIRED — root cause needs a single subject).
    public static readonly Tool TraceRootCause = new("trace_root_cause");

    // The unified knowledge-search tool: the operator's indexed docs first, the public web as a
    // fallback. IWebSearch is an internal capability this aggregator composes, not a tool the model
    // picks directly. Offered iff a source backs it (SearchOptions.Available); the dispatcher routes
    // it to ISearch.
    public static readonly Tool Search = new("search");

    // Reads ONE specific web page by URL — a leaf capability distinct from `search`: search FINDS
    // pages via provider-summarized hits, this READS a page the model already has (or just found)
    // the URL for (an official docs page, a Steam store page, a raw Dockerfile). Offered iff a
    // fetch adapter is configured (FetchOptions.Available), same omit-when-disabled rule as
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

    // Authorized reads (offered only to action-authorized callers — they expose file and console
    // contents, so they are gated like the command tier even though they mutate nothing).
    public static readonly Tool ReadFile = new("read_file");
    public static readonly Tool ListFiles = new("list_files");

    // Locates a file by name pattern anywhere under the server's directory. A game's own config sits
    // at a game-specific depth (Palworld's is five levels down), and discovering it one listing at a
    // time costs more agent iterations than a turn has — which is what left file edits unproposed.
    public static readonly Tool FindFiles = new("find_files");

    // The content counterpart to find_files: a caller often knows the SETTING it wants to change but
    // not which file holds it, which no amount of name matching answers.
    public static readonly Tool SearchFiles = new("search_files");

    // The supervisor's captured console output for one instance. Its own tool rather than a
    // server_info aspect: console output is file-like content and belongs in this tier, and a tool is
    // offered wholly or not at all — riding it on an open-tier read would expose it to callers who
    // cannot read a log file.
    public static readonly Tool ReadConsole = new("read_console");

    // Staged commands — propose-only. The model never executes these; the dispatcher resolves +
    // STAGES them, and a human confirms before they run.

    // The lifecycle actions are ONE tool with a `verb` parameter — collapsed from five near-identical
    // tools so the small local model faces fewer, less-overlapping choices. install/uninstall stay
    // separate (different params + confirm tiers); set_config_value carries a key/value.
    public static readonly Tool ServerCommand = new("server_command");

    // The backup verbs that act on an EXISTING archive. Creating one is a lifecycle action and lives
    // on server_command; listing them is a read and lives on server_info.
    public static readonly Tool BackupCommand = new("backup_command");

    // Player moderation. The target's FORM is a fact about the game (a name or an account id), which
    // the dispatcher supplies from the blueprint rather than letting the model guess it.
    public static readonly Tool PlayerCommand = new("player_command");

    public static readonly Tool InstallServer = new("install_server");
    public static readonly Tool UninstallServer = new("uninstall_server");
    public static readonly Tool SetConfigValue = new("set_config_value");

    // Propose overwriting a text file inside a server's OWN directory with COMPLETE new content —
    // e.g. a game's own config (Palworld's PalWorldSettings.ini), never KGSM's .config.ini (that's
    // set_config_value). Staged for human confirmation like every command; the confirm step shows a
    // preview/diff before it replaces the file. Game-agnostic: it handles whatever text the model
    // composes (a game's own INI/JSON/tuple format), so there is no per-game structure to parse.
    public static readonly Tool WriteFile = new("write_file");

    /// <summary>
    /// The aspects <see cref="ServerInfo"/> accepts, in display order. Single source of truth for
    /// both the tool's <c>enum</c> schema and the dispatcher's aspect routing, so the catalog and the
    /// dispatcher cannot drift.
    /// <para>
    /// There is deliberately no <c>ports</c> aspect: <see cref="GetNetwork"/> owns the port question,
    /// and two tools that both look like they answer it is the overlap this catalog exists to remove.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> ServerInfoAspects =
        new[] { "status", "config", "version", "players", "backups", "note", "autostart" };

    /// <summary>The aspects <see cref="HostInfo"/> accepts. <c>vitals</c> is the default.</summary>
    public static readonly IReadOnlyList<string> HostInfoAspects =
        new[] { "vitals", "ports", "conflicts" };

    /// <summary>The scopes <see cref="Events"/> accepts. <c>all</c> is the default.</summary>
    public static readonly IReadOnlyList<string> EventScopes = new[] { "all", "changes" };

    /// <summary>
    /// The verbs <see cref="ServerCommand"/> accepts, in display order. Single source of
    /// truth for both the tool's <c>enum</c> schema and the dispatcher's verb→kind routing
    /// (<see cref="ServerCommandKind"/>).
    /// </summary>
    public static readonly IReadOnlyList<string> ServerCommandVerbs =
        new[] { "start", "stop", "restart", "update", "backup", "enable_autostart", "disable_autostart" };

    /// <summary>The verbs <see cref="BackupCommand"/> accepts.</summary>
    public static readonly IReadOnlyList<string> BackupCommandVerbs =
        new[] { "restore", "delete", "prune" };

    /// <summary>The verbs <see cref="PlayerCommand"/> accepts.</summary>
    public static readonly IReadOnlyList<string> PlayerCommandVerbs =
        new[] { "kick", "ban", "unban" };

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
            "enable_autostart" => ConfirmationKind.AutostartEnable,
            "disable_autostart" => ConfirmationKind.AutostartDisable,
            _ => null,
        };

    /// <summary>Maps a <see cref="BackupCommand"/> verb token to its <see cref="ConfirmationKind"/>.</summary>
    public static ConfirmationKind? BackupCommandKind(string? verb) =>
        verb?.Trim().ToLowerInvariant() switch
        {
            "restore" => ConfirmationKind.BackupRestore,
            "delete" => ConfirmationKind.BackupDelete,
            "prune" => ConfirmationKind.BackupPrune,
            _ => null,
        };

    /// <summary>Maps a <see cref="PlayerCommand"/> verb token to its <see cref="ConfirmationKind"/>.</summary>
    public static ConfirmationKind? PlayerCommandKind(string? verb) =>
        verb?.Trim().ToLowerInvariant() switch
        {
            "kick" => ConfirmationKind.PlayerKick,
            "ban" => ConfirmationKind.PlayerBan,
            "unban" => ConfirmationKind.PlayerUnban,
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

    private static readonly LlmToolParameter EventsInstanceName = new(
        "instance_name",
        "Optional. Scope the events to this one server. OMIT to get events across every server on " +
        "this host — do that for questions like \"what happened recently?\" / \"any errors lately?\".",
        Required: false);

    private static readonly LlmToolParameter EventsScope = new(
        "scope",
        "Optional, defaults to \"all\". Use \"all\" for the full feed — starts, stops, crashes, " +
        "installs, updates, backups, port changes, player activity. Use \"changes\" to narrow it to " +
        "durable CHANGES only (installs, uninstalls, updates, version changes, backups, port " +
        "changes), which is the right scope for \"what changed on X?\" or \"when was X last updated?\".",
        Required: false,
        AllowedValues: EventScopes);

    private static readonly LlmToolParameter EventsWindow = new(
        "window",
        "Optional. How far back to look. Defaults to 24h if omitted.",
        Required: false,
        AllowedValues: AuditWindow.AllowedValues);

    private static readonly LlmToolParameter RootCauseRange = new(
        "range",
        "Optional. How far back to look for evidence. Defaults to 24h if omitted.",
        Required: false,
        AllowedValues: AuditWindow.AllowedValues);

    private static readonly LlmToolParameter ServerInfoInstanceName = new(
        "instance_name",
        "The server to report on. OMIT this to cover EVERY server at once — always do that for " +
        "questions like \"which servers are running?\" instead of checking servers one at a time.",
        Required: false);

    private static readonly LlmToolParameter ServerInfoAspect = new(
        "aspect",
        "Optional, defaults to \"status\" (is it running, and its headline detail). Pick another to " +
        "answer a narrower question: \"config\" a summary of its KGSM settings (to see the raw " +
        "configuration file, use read_file), \"version\" the installed version and whether an update " +
        "is available, \"players\" who is connected right now, \"backups\" the backups it has, " +
        "\"note\" its operator note, \"autostart\" whether it starts at boot.",
        Required: false,
        AllowedValues: ServerInfoAspects);

    private static readonly LlmToolParameter HostInfoAspect = new(
        "aspect",
        "Optional, defaults to \"vitals\" (uptime, load, memory, disk, external IP, whether a reboot " +
        "is pending). Use \"ports\" for what is currently bound on the host, or \"conflicts\" for " +
        "servers configured to want the same port.",
        Required: false,
        AllowedValues: HostInfoAspects);

    private static readonly LlmToolParameter BlueprintName = new(
        "blueprint_name", "The game type (blueprint) to install, from the installable list.");

    private static readonly LlmToolParameter BlueprintInfoName = new(
        "blueprint_name",
        "Optional. A game type to describe in detail — its ports, resource needs, and what it " +
        "supports. OMIT it to list every game type that can be installed.",
        Required: false);

    private static readonly LlmToolParameter OptionalInstanceName = new(
        "instance_name", "Optional custom name for the new instance. Omit to let the system name it.",
        Required: false);

    private static readonly LlmToolParameter InstallVersion = new(
        "version",
        "Optional. A specific version to install. Omit for the latest, which is almost always right.",
        Required: false);

    private static readonly LlmToolParameter InstallPort = new(
        "port",
        "Optional. Override the blueprint's primary game port. Omit unless the user asked for a " +
        "specific port or a conflict needs avoiding.",
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
        "Which action to take on the server: start, stop, restart, update, backup (take one now), " +
        "enable_autostart (make it start when the host boots), or disable_autostart. Note that " +
        "start and enable_autostart are different: start runs it NOW, enable_autostart only affects " +
        "the next boot.",
        AllowedValues: ServerCommandVerbs);

    private static readonly LlmToolParameter BackupVerb = new(
        "verb",
        "What to do with the server's existing backups: restore (replace the server's current data " +
        "with a backup), delete (remove one backup), or prune (delete the oldest, keeping the most " +
        "recent few). To CREATE a backup use server_command with verb=backup instead.",
        AllowedValues: BackupCommandVerbs);

    private static readonly LlmToolParameter BackupName = new(
        "backup_name",
        "Which backup to act on, by its id from server_info(aspect=backups). Required for restore " +
        "and delete; omit it for prune.",
        Required: false);

    private static readonly LlmToolParameter BackupKeep = new(
        "keep",
        "For prune only: how many of the most recent backups to keep. Omit for the server's " +
        "configured retention.",
        Required: false);

    private static readonly LlmToolParameter PlayerVerb = new(
        "verb",
        "What to do with the player: kick (disconnect them now), ban (disconnect and block them), " +
        "or unban (lift a ban).",
        AllowedValues: PlayerCommandVerbs);

    private static readonly LlmToolParameter PlayerTarget = new(
        "target",
        "Which player, exactly as the user named them. Use server_info(aspect=players) first if you " +
        "need to see who is connected. Do NOT convert the name into an id or invent one — the game " +
        "decides which form it needs and the system supplies it.");

    private static readonly LlmToolParameter ReadPath = new(
        "path",
        "Optional. The file to read, as a path relative to the server's own directory — e.g. " +
        "\"server.properties\" or \"logs/latest.log\". OMIT it to read the server's main " +
        "configuration (its .config.ini). Use list_files first if you don't know the file name.",
        Required: false);

    private static readonly LlmToolParameter ListSubdir = new(
        "subdir",
        "Optional. A subdirectory to list, relative to the server's own directory — e.g. \"logs\" " +
        "or \"install\". OMIT it to list the server's top level.",
        Required: false);

    private static readonly LlmToolParameter FindPattern = new(
        "pattern",
        "The file name to look for, as a glob — e.g. \"PalWorldSettings.ini\", \"*.ini\", " +
        "\"server.properties\", or \"*Settings*\". Include a \"/\" to match on the path instead of " +
        "just the name (e.g. \"*/Config/*.ini\"). Prefer the most specific pattern you can: a broad " +
        "one can match hundreds of files.");

    private static readonly LlmToolParameter FindSubdir = new(
        "subdir",
        "Optional. Search only inside this subdirectory of the server's folder. OMIT it to search the " +
        "whole server directory, which is usually what you want.",
        Required: false);

    // Named "text", not "pattern", because find_files takes a "pattern" that is a filename GLOB: one
    // argument name across two tools carrying two syntaxes had the model feeding "*Player*" to the
    // content searcher, where a leading quantifier is not a valid expression at all.
    private static readonly LlmToolParameter SearchFilesText = new(
        "text",
        "The text to look for inside the files — a setting name like \"MaxPlayers\" or " +
        "\"DayTimeSpeedRate\". Matching ignores case. This is text, NOT a filename pattern: do not " +
        "wrap it in \"*\". A regular expression works too. Prefer a distinctive string: a common word " +
        "can match hundreds of lines.");

    private static readonly LlmToolParameter ConsoleLines = new(
        "lines",
        "Optional. How many of the most recent console lines to read. Defaults to a recent tail.",
        Required: false);

    private static readonly LlmToolParameter ConsoleRun = new(
        "run",
        "Optional. Which run of the server to read, newest first: 0 is the current run (the default), "
        + "1 the run before it, and so on. A server's log restarts from empty every time it starts, so "
        + "after a crash-restart the crash is in run 1 while run 0 holds only the clean boot that "
        + "followed it.",
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

    private static readonly LlmToolParameter SearchScopeParam = new(
        "scope",
        "Where to look. Use \"web\" whenever the user asked you to check ONLINE or on the internet, " +
        "or when the answer depends on what is true right now — a release date, a current version, " +
        "recent news, anything that changes over time. Use \"local\" to stay inside the operator's " +
        "own documentation. OMIT it (or \"auto\") otherwise, which tries the documentation first. " +
        "NOTE: local guides about a game will match a question about that game even when they say " +
        "nothing about what was asked, so \"auto\" is the wrong choice for anything time-sensitive.",
        Required: false,
        AllowedValues: ["auto", "local", "web"]);

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
        LlmToolDefinition.Create(ServerInfo,
            "Look up a game server. With NO instance_name: covers every server at once — this is the " +
            "right tool for \"what's running?\" / \"list the servers\". With instance_name: reports on " +
            "that one server. The 'aspect' parameter picks WHAT to report (status by default, or its " +
            "config, version, connected players, backups, note, or autostart setting) — use it instead " +
            "of reading files to answer those.",
            ServerInfoInstanceName, ServerInfoAspect),

        LlmToolDefinition.Create(HostInfo,
            "Report on the HOST MACHINE itself rather than any one server — its uptime, load, memory, " +
            "disk space, external IP address and whether a reboot is pending. Use \"ports\" to see " +
            "what is bound on the host and \"conflicts\" to find servers configured to want the same " +
            "port. Use this for \"is the machine running out of space/memory?\", \"how busy is the " +
            "host?\", or \"is anything fighting over a port?\".",
            HostInfoAspect),

        LlmToolDefinition.Create(BlueprintInfo,
            "Look up the game types (blueprints) that can be installed. With NO blueprint_name: lists " +
            "all of them. With one: describes that game type in detail — the ports it needs, its " +
            "resource requirements, and what it supports. Use this to answer \"what games can I run?\" " +
            "or \"what does Valheim need?\" BEFORE proposing an install.",
            BlueprintInfoName),

        LlmToolDefinition.Create(Events,
            "Get the history of what has happened, read straight from KGSM's own event log — most " +
            "recent first. Covers one server (with instance_name) or every server (without). Use the " +
            "default scope for \"what happened to X?\" / \"has X crashed recently?\", and " +
            "scope=changes for \"what changed on X?\" or \"when was X last updated?\".",
            EventsInstanceName, EventsScope, EventsWindow),

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

        LlmToolDefinition.Create(TraceRootCause,
            "Investigate WHY one server crashed, won't stay up, or has been misbehaving recently — " +
            "an incident/history question, not a right-now check (use run_health_check for \"is X " +
            "healthy now?\"). Automatically pulls together its event history, resource usage, and " +
            "health checks and matches them against known failure patterns (port conflicts, " +
            "update-triggered crash loops, disk-full failures, event-log/live-state mismatches), " +
            "returning a ranked explanation with the evidence behind it. Use this for \"why did X " +
            "crash?\" or \"why did X stop working?\" instead of calling events/get_performance " +
            "separately and reasoning it out yourself. If no known pattern matches, it honestly " +
            "reports a correlation instead of guessing a cause.",
            InstanceName, RootCauseRange),

        LlmToolDefinition.Create(Search,
            "Look something up in the knowledge base: the operator's indexed documentation first, then " +
            "the public web as a fallback. Returns short passages, each with its source (a doc path or " +
            "a URL). Use it ONLY for outside facts that help with the games/servers — a game's latest " +
            "version, patch notes, or what a setting does. Results may be external and out of date, so " +
            "cite the sources and don't state them as certain. Do NOT use this for anything about this " +
            "host's own servers (status, config, health) — the KGSM tools are authoritative for those. " +
            "When the user asks you to look something up ONLINE, pass scope=\"web\" — otherwise a local " +
            "guide about the same game will answer instead, and it will not know anything current.",
            SearchQuery, SearchScopeParam),

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
    /// Reads that expose file or console contents — offered only to action-authorized callers.
    /// They mutate nothing (no per-message cap, no staging), but reading a server's files reveals
    /// more than the read-only tier, so the gate refuses them for unauthorized callers as
    /// defense-in-depth. (Conservative on purpose: tightening after exposure is the harder
    /// direction.)
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

        LlmToolDefinition.Create(FindFiles,
            "Find a file inside a game server's folder by name, searching every subdirectory at once. " +
            "USE THIS INSTEAD OF list_files when you know roughly what the file is called — a game's " +
            "own config is often several directories deep, and stepping down one directory at a time " +
            "wastes the turn. Give a glob like \"PalWorldSettings.ini\", \"*.ini\" or " +
            "\"server.properties\"; it returns matching paths ready to hand to read_file. Archived " +
            "copies under a backups folder are excluded, so what you get is the live file.",
            InstanceName, FindPattern, FindSubdir),

        LlmToolDefinition.Create(SearchFiles,
            "Search INSIDE a game server's files for text, across every subdirectory at once — use " +
            "this when you know the SETTING you want but not which file holds it (e.g. " +
            "\"MaxPlayers\", \"DayTimeSpeedRate\"). Returns each matching file with the line number " +
            "and the line itself, ready to hand to read_file. Use find_files instead when you know " +
            "the file's NAME. Archived copies under a backups folder are excluded, and binary files " +
            "are skipped.",
            InstanceName, SearchFilesText, FindSubdir),

        LlmToolDefinition.Create(ReadConsole,
            "Read what a server itself printed — its console output. Use this for what a server is " +
            "saying right now, or for what it said on its way down. A stopped server's last output is " +
            "still readable. The reply says which run it came from and whether the server restarted " +
            "recently; if it did, the reason it went down is in the previous run, which you read by " +
            "calling this again with run=1.",
            InstanceName, ConsoleLines, ConsoleRun),
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
            "turn the user asks — do NOT run a separate search/fetch_url or blueprint_info first, and do " +
            "NOT announce that you'll go research it and come back; calling this tool IS how the research " +
            "and drafting start. After it returns, tell the user a draft is ready for them to " +
            "review and save; do NOT claim the game is added or installed. Use this ONLY when the game " +
            "is not in blueprint_info / not offered by install_server — for a game that's already " +
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
    /// Every server command — all propose-only. The dispatcher STAGES each for human confirmation;
    /// none runs in the agent loop. Descriptions say so explicitly, so the model narrates
    /// "I've proposed…", never "I've done it."
    /// </summary>
    public static readonly IReadOnlyList<LlmToolDefinition> StagedCommands = new[]
    {
        LlmToolDefinition.Create(ServerCommand,
            "Propose an action on an EXISTING server instance — choose it with the 'verb' parameter: " +
            "start (a stopped server), stop (a running one), restart (stop then start), update (to its " +
            "latest version), backup (create one now), enable_autostart / disable_autostart (whether it " +
            "starts when the host boots). Staged for human confirmation — it does not run until a " +
            "person confirms.",
            InstanceName, ServerVerb),

        LlmToolDefinition.Create(BackupCommand,
            "Propose acting on a server's EXISTING backups: restore one over the server's current data, " +
            "delete one, or prune the old ones. List them with server_info(aspect=backups) first so you " +
            "name a backup that exists. Restoring REPLACES the server's current data and deleting is " +
            "permanent. Staged for human confirmation — it does not run until a person confirms.",
            InstanceName, BackupVerb, BackupName, BackupKeep),

        LlmToolDefinition.Create(PlayerCommand,
            "Propose moderating a player on a server: kick, ban, or unban them. Staged for human " +
            "confirmation — it does not run until a person confirms. Only works on games whose server " +
            "supports moderation commands; if it doesn't, say so rather than suggesting a workaround.",
            InstanceName, PlayerVerb, PlayerTarget),

        LlmToolDefinition.Create(InstallServer,
            "Propose installing a NEW game server from a blueprint. Heavy and slow; staged for " +
            "human confirmation — it does not run until a person confirms.",
            BlueprintName, OptionalInstanceName, InstallVersion, InstallPort),

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
