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

    // The per-instance reads. One tool per fact, each named for what it returns, rather than one tool
    // with an `aspect` enum. The enum's premise was that fewer, less-overlapping choices route better
    // on a small local model; a name that states its own answer is the competing premise, and the
    // routing benchmark is what decides between them. Every one of these still reaches the same
    // handler it did as an aspect — only how it is offered changed.
    //
    // Omitting instance_name covers the whole fleet on the four that support it (status, players,
    // autostart, backups); the rest report on one instance.
    public static readonly Capability ServerInfo = new("instance.status");
    public static readonly Capability GetInstanceConfig = new("instance.config");
    public static readonly Capability GetInstanceVersion = new("instance.version");
    public static readonly Capability ListOnlinePlayers = new("instance.players");
    public static readonly Capability ListInstanceBackups = new("instance.backups");
    public static readonly Capability GetInstanceNote = new("instance.note");
    public static readonly Capability GetInstanceAutostart = new("instance.autostart");

    // The host itself — the machine, not any one instance — split on the same principle.
    public static readonly Capability HostInfo = new("host.vitals");
    public static readonly Capability ListHostPorts = new("host.ports");
    public static readonly Capability FindPortConflicts = new("host.port-conflicts");

    // The catalog read: every installable game type, or one game type's detail.
    public static readonly Capability BlueprintInfo = new("blueprint.info");

    // Engine event history, read straight from the engine's event journal (never via kgsm-api — the
    // assistant is a leaf). The whole timeline, always: it used to carry a `scope` that narrowed the
    // feed to state-changing entries, which asked the model to predict what the answer would contain
    // before it had seen any of it. It predicted wrong in both directions — "when was X last updated"
    // went to the web because the update lived in the narrow scope, and "did you restart X" missed the
    // restart because it does not. One feed, and the model reads what it needs off it.
    public static readonly Capability Events = new("events.history");

    public static readonly Capability RunHealthCheck = new("instance.health");

    // Live per-server resource usage (CPU/memory/network/disk-io/pids) — a snapshot of current
    // measured values from the metrics monitor. Status-sensitive like health, so it's a read-only
    // tool offered to everyone, not a file-content read.
    public static readonly Capability GetPerformance = new("instance.resources");

    // Network reachability for one instance across two layers: the HOST FIREWALL (the ports KGSM has
    // opened, the firewall backend, its enforcement state — from the kgsm-firewall authority) AND the
    // NOTE: there is no per-instance network capability. "Is it up" and "can anyone reach it" are one
    // question, answered by three authorities — the engine for the configured ports, the host firewall
    // for what is open, the supervisor for what the router forwards. get_instance_status reads all
    // three and reports them together, so the answer costs one call. A second tool over the same
    // ground was the ambiguity the catalog's naming pass exists to remove: kgsm's status payload
    // carried the ports regardless, so both tools answered "what port is X on?" and routing flipped
    // between them run to run.

    // The capstone aggregator: a DETERMINISTIC composition of the event timeline + a metrics window +
    // a health snapshot for ONE instance, run through a fixed rules table of known KGSM failure
    // signatures. No nested model call — the model only narrates the finding this tool already
    // computed (RootCause.RootCauseAggregator). Per-instance only (unlike events, instance_name is
    // REQUIRED — root cause needs a single subject).
    public static readonly Capability TraceRootCause = new("instance.root-cause");

    // The unified knowledge-search tool: the operator's indexed docs first, the public web as a
    // fallback. IWebSearch is an internal capability this aggregator composes, not a tool the model
    // picks directly. Offered iff a source backs it (SearchOptions.Available); the dispatcher routes
    // it to ISearch.
    public static readonly Capability Search = new("knowledge.search");

    // Reads ONE specific web page by URL — a leaf capability distinct from `search`: search FINDS
    // pages via provider-summarized hits, this READS a page the model already has (or just found)
    // the URL for (an official docs page, a Steam store page, a raw Dockerfile). Offered iff a
    // fetch adapter is configured (FetchOptions.Available), same omit-when-disabled rule as
    // `search`; the dispatcher routes it to IWebFetch.
    public static readonly Capability FetchUrl = new("web.fetch");

    // Authorized, autonomous action — offered only to action-authorized callers, but unlike the staged
    // commands below it runs INLINE (no propose→confirm): it touches no user data (it only researches,
    // then test-installs and tears down a disposable probe of its own), so there is nothing for a human
    // to confirm. See LlmTools.AuthorizedActions for why this needs its own tier.
    public static readonly Capability CreateBlueprint = new("blueprint.create");

    // Updates the blueprint draft the user is currently reviewing in the editor — re-validates the
    // supplied full YAML and re-shows it as a fresh draft. Offered ONLY on a turn that carries an open
    // draft (ServerAssistant filters it out otherwise), so the model can act on the current content the
    // SPA sends rather than fabricating a "changed" draft it has no way to actually mutate.
    public static readonly Capability ReviseBlueprint = new("blueprint.revise");

    // Authorized reads (offered only to action-authorized callers — they expose file and console
    // contents, so they are gated like the command tier even though they mutate nothing).
    public static readonly Capability ReadFile = new("files.read");
    public static readonly Capability ListFiles = new("files.list");

    // Locates a file by name pattern anywhere under the server's directory. A game's own config sits
    // at a game-specific depth (Palworld's is five levels down), and discovering it one listing at a
    // time costs more agent iterations than a turn has — which is what left file edits unproposed.
    public static readonly Capability FindFiles = new("files.find");

    // The content counterpart to find_files: a caller often knows the SETTING it wants to change but
    // not which file holds it, which no amount of name matching answers.
    public static readonly Capability SearchFiles = new("files.search");

    // The supervisor's captured console output for one instance. Its own tool rather than a
    // server_info aspect: console output is file-like content and belongs in this tier, and a tool is
    // offered wholly or not at all — riding it on an open-tier read would expose it to callers who
    // cannot read a log file.
    public static readonly Capability ReadConsole = new("instance.console");

    // Staged commands — propose-only. The model never executes these; the dispatcher resolves +
    // STAGES them, and a human confirms before they run.

    // The lifecycle actions are ONE tool with a `verb` parameter — collapsed from five near-identical
    // tools so the small local model faces fewer, less-overlapping choices. install/uninstall stay
    // separate (different params + confirm tiers); set_config_value carries a key/value.
    public static readonly Capability ServerCommand = new("instance.lifecycle");

    // The backup verbs that act on an EXISTING archive. Creating one is a lifecycle action and lives
    // on server_command; listing them is a read and lives on server_info.
    public static readonly Capability BackupCommand = new("backups.manage");

    // Player moderation. The target's FORM is a fact about the game (a name or an account id), which
    // the dispatcher supplies from the blueprint rather than letting the model guess it.
    public static readonly Capability PlayerCommand = new("player.moderate");

    public static readonly Capability InstallServer = new("instance.install");
    public static readonly Capability UninstallServer = new("instance.uninstall");
    public static readonly Capability SetConfigValue = new("instance.kgsm-setting");

    // Propose one anchored replacement inside a text file in a server's OWN directory — e.g. a game's
    // own config (Palworld's PalWorldSettings.ini), never KGSM's .config.ini (that's set_config_value).
    // The model sends the text to replace and its replacement; the file's other bytes are read from
    // disk and never routed through it. Staged for human confirmation like every command; the confirm
    // step shows a preview/diff before it replaces the file. Game-agnostic — an anchor is just text, so
    // there is no per-game structure to parse.
    public static readonly Capability WriteFile = new("files.edit");

    // Changes ONE Key=Value setting in a game's own config, addressed by the key. write_file asks for
    // the text being replaced, which a packed config defeats: Palworld keeps every setting on a single
    // ~2000-character line, so naming the text means echoing that whole line back byte-perfect, and a
    // 12B model mangles a key or a decimal somewhere in the middle and retries until the iteration cap.
    // Naming the key sends a dozen characters and reads the current value off disk. Same staged
    // ConfirmationKind.WriteFile as write_file — the proposal is the same file with the same preview;
    // only how the caller addressed it differs.
    public static readonly Capability SetGameSetting = new("instance.game-setting");

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
    public static readonly IReadOnlyList<Capability> ReadOnlyTier =
    [
        ServerInfo, GetInstanceConfig, GetInstanceVersion, ListOnlinePlayers,
        ListInstanceBackups, GetInstanceNote, GetInstanceAutostart,
        HostInfo, ListHostPorts, FindPortConflicts,
        BlueprintInfo, Events, RunHealthCheck,
        GetPerformance, TraceRootCause, Search, FetchUrl,
    ];

    /// <summary>Reads that expose file or console contents — authorized callers only.</summary>
    public static readonly IReadOnlyList<Capability> AuthorizedReadOnlyTier =
    [
        ReadFile, ListFiles, FindFiles, SearchFiles, ReadConsole,
    ];

    /// <summary>Authorized, mutating and confirm-free — run inline, never staged.</summary>
    public static readonly IReadOnlyList<Capability> AuthorizedActionsTier =
    [
        CreateBlueprint, ReviseBlueprint,
    ];

    /// <summary>Every server command — propose-only, always staged for a human confirmation.</summary>
    public static readonly IReadOnlyList<Capability> StagedCommandsTier =
    [
        ServerCommand, BackupCommand, PlayerCommand, InstallServer,
        UninstallServer, SetConfigValue, WriteFile, SetGameSetting,
    ];

    /// <summary>Tools of authorized-only reads; refused for unauthorized callers, but not capped.</summary>
    public static readonly IReadOnlySet<Capability> AuthorizedReadTools = AuthorizedReadOnlyTier.ToHashSet();

    /// <summary>
    /// Tools of the propose-only commands; offered only to authorized callers, staged
    /// (never executed inline), and counted against the per-message staging cap.
    /// </summary>
    public static readonly IReadOnlySet<Capability> StagedCommandTools = StagedCommandsTier.ToHashSet();

    /// <summary>Tools of the authorized, autonomous (confirm-free) actions; refused for unauthorized
    /// callers, run inline (never staged). Includes the draft-only <see cref="ReviseBlueprint"/>
    /// (offered conditionally, but the same authorized+inline tier when it IS offered).</summary>
    public static readonly IReadOnlySet<Capability> AuthorizedActionTools = AuthorizedActionsTier.ToHashSet();

    /// <summary>
    /// The ordinary-turn OFFER: every tool an authorized caller is given on a turn carrying no draft.
    /// <see cref="ReviseBlueprint"/> is excluded because it is offered only alongside an open draft.
    /// </summary>
    public static IReadOnlySet<Capability> EveryOfferedCapability =>
        ReadOnlyTier.Concat(AuthorizedReadOnlyTier).Concat(StagedCommandsTier)
            .Concat(AuthorizedActionsTier).Where(t => t != ReviseBlueprint).ToHashSet();

    /// <summary>
    /// Every tool this assistant can EVER dispatch, including the ones offered only in context —
    /// today just <see cref="ReviseBlueprint"/>, appended on a turn that carries an open draft.
    /// Distinct from <see cref="EveryOfferedCapability"/>, which is the ordinary-turn OFFER: asking "did this
    /// tool ever exist?" of the offer set would report a conditionally-offered tool as one the model
    /// invented. This is also the set a tools.json entry has to name to be accepted.
    /// </summary>
    public static IReadOnlySet<Capability> EveryCapability =>
        ReadOnlyTier.Concat(AuthorizedReadOnlyTier).Concat(StagedCommandsTier)
            .Concat(AuthorizedActionsTier).ToHashSet();

    public static bool IsStagedCommand(Capability tool) => StagedCommandTools.Contains(tool);

    public static bool IsAuthorizedRead(Capability tool) => AuthorizedReadTools.Contains(tool);

    public static bool IsAuthorizedAction(Capability tool) => AuthorizedActionTools.Contains(tool);
}
