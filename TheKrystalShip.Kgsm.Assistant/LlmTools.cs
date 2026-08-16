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

    // Propose one anchored replacement inside a text file in a server's OWN directory — e.g. a game's
    // own config (Palworld's PalWorldSettings.ini), never KGSM's .config.ini (that's set_config_value).
    // The model sends the text to replace and its replacement; the file's other bytes are read from
    // disk and never routed through it. Staged for human confirmation like every command; the confirm
    // step shows a preview/diff before it replaces the file. Game-agnostic — an anchor is just text, so
    // there is no per-game structure to parse.
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

    // ── Tier membership: the AUTHORIZATION boundary ──────────────────────────────
    //
    // Which tier a tool sits in decides who is offered it and whether it is staged, so it stays in
    // code. The prose and schema live on disk and are edited freely; moving a staged command into
    // the read-only tier would be a privilege escalation, and that is not an edit a text file gets
    // to make. Each name here must have a handler in ToolDispatcher and an entry in tools.json —
    // DiskToolCatalog refuses to start the service if either is missing.

    /// <summary>Read-only tools, offered to everyone.</summary>
    public static readonly IReadOnlyList<Tool> ReadOnlyTier =
    [
        ServerInfo, HostInfo, BlueprintInfo, Events, RunHealthCheck,
        GetPerformance, GetNetwork, TraceRootCause, Search, FetchUrl,
    ];

    /// <summary>Reads that expose file or console contents — authorized callers only.</summary>
    public static readonly IReadOnlyList<Tool> AuthorizedReadOnlyTier =
    [
        ReadFile, ListFiles, FindFiles, SearchFiles, ReadConsole,
    ];

    /// <summary>Authorized, mutating and confirm-free — run inline, never staged.</summary>
    public static readonly IReadOnlyList<Tool> AuthorizedActionsTier =
    [
        CreateBlueprint, ReviseBlueprint,
    ];

    /// <summary>Every server command — propose-only, always staged for a human confirmation.</summary>
    public static readonly IReadOnlyList<Tool> StagedCommandsTier =
    [
        ServerCommand, BackupCommand, PlayerCommand, InstallServer,
        UninstallServer, SetConfigValue, WriteFile,
    ];

    /// <summary>Tools of authorized-only reads; refused for unauthorized callers, but not capped.</summary>
    public static readonly IReadOnlySet<Tool> AuthorizedReadTools = AuthorizedReadOnlyTier.ToHashSet();

    /// <summary>
    /// Tools of the propose-only commands; offered only to authorized callers, staged
    /// (never executed inline), and counted against the per-message staging cap.
    /// </summary>
    public static readonly IReadOnlySet<Tool> StagedCommandTools = StagedCommandsTier.ToHashSet();

    /// <summary>Tools of the authorized, autonomous (confirm-free) actions; refused for unauthorized
    /// callers, run inline (never staged). Includes the draft-only <see cref="ReviseBlueprint"/>
    /// (offered conditionally, but the same authorized+inline tier when it IS offered).</summary>
    public static readonly IReadOnlySet<Tool> AuthorizedActionTools = AuthorizedActionsTier.ToHashSet();

    /// <summary>
    /// The ordinary-turn OFFER: every tool an authorized caller is given on a turn carrying no draft.
    /// <see cref="ReviseBlueprint"/> is excluded because it is offered only alongside an open draft.
    /// </summary>
    public static IReadOnlySet<Tool> AllToolNames =>
        ReadOnlyTier.Concat(AuthorizedReadOnlyTier).Concat(StagedCommandsTier)
            .Concat(AuthorizedActionsTier).Where(t => t != ReviseBlueprint).ToHashSet();

    /// <summary>
    /// Every tool this assistant can EVER dispatch, including the ones offered only in context —
    /// today just <see cref="ReviseBlueprint"/>, appended on a turn that carries an open draft.
    /// Distinct from <see cref="AllToolNames"/>, which is the ordinary-turn OFFER: asking "did this
    /// tool ever exist?" of the offer set would report a conditionally-offered tool as one the model
    /// invented. This is also the set a tools.json entry has to name to be accepted.
    /// </summary>
    public static IReadOnlySet<Tool> EveryToolName =>
        ReadOnlyTier.Concat(AuthorizedReadOnlyTier).Concat(StagedCommandsTier)
            .Concat(AuthorizedActionsTier).ToHashSet();

    public static bool IsStagedCommand(Tool tool) => StagedCommandTools.Contains(tool);

    public static bool IsAuthorizedRead(Tool tool) => AuthorizedReadTools.Contains(tool);

    public static bool IsAuthorizedAction(Tool tool) => AuthorizedActionTools.Contains(tool);
}
