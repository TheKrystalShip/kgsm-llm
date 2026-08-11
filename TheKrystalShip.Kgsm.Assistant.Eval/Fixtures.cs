using TheKrystalShip.Kgsm.Assistant.Ports;

namespace TheKrystalShip.Kgsm.Assistant.Eval;

/// <summary>
/// A role a benchmark case needs filled by some real server on the host. Cases speak in roles, not
/// hardcoded game names, so the same corpus runs against any host over time — the eval that lost a
/// battery to a hardcoded/hidden inventory is exactly what this avoids.
/// </summary>
internal enum FixtureRole
{
    /// <summary>An instance that is the ONLY one of its game type — so a bare game word resolves it
    /// unambiguously. The load-bearing fixture for the "resolve a unique match, don't ask" cases.</summary>
    UniqueGame,

    /// <summary>Any instance currently running (authoritative is-active).</summary>
    Running,

    /// <summary>Any instance currently stopped.</summary>
    Stopped,

    /// <summary>At least one instance exists (for fleet-wide questions).</summary>
    AnyInstance,

    /// <summary>A blueprint with no installed instance — an installable-but-absent game.</summary>
    NeverInstalledGame,

    /// <summary>Two or more instances exist — the precondition for a GENUINELY ambiguous reference.</summary>
    MultipleInstances,

    /// <summary>A unique-game instance whose blueprint declares player-moderation commands — the
    /// positive half of the moderation pair, where staging a kick is the correct trajectory.</summary>
    ModeratableGame,

    /// <summary>A unique-game instance whose blueprint declares NO moderation commands — the negative
    /// half, where the only correct trajectory is to stage nothing and say the game can't.</summary>
    NoModerationGame,

    /// <summary>
    /// An instance whose roster is genuinely UNOBSERVABLE — the game reports nothing the supervisor
    /// can read, so presence comes back <see cref="PresenceDetection.None"/>. The load-bearing
    /// fabrication fixture: "nobody is online" is the correct answer for a measured-empty server and
    /// an invented one here, so a case on this role separates the two.
    /// </summary>
    NoPresenceGame,
}

/// <summary>One installed instance with its game type and authoritative run-state.</summary>
internal sealed record InstanceFact(string Name, string Game, bool Running);

/// <summary>
/// The host's live inventory resolved into concrete values for each <see cref="FixtureRole"/>.
/// Roles that can't be filled are left null; a case requiring an unfilled role is skipped (reported),
/// never silently failed.
/// </summary>
internal sealed record ResolvedFixtures(
    IReadOnlyList<InstanceFact> Instances,
    IReadOnlyList<string> Blueprints,
    string? UniqueGameWord,
    string? UniqueGameInstance,
    string? RunningInstance,
    string? StoppedInstance,
    string? AnyInstance,
    string? NeverInstalledGame,
    string? ModeratableGameWord = null,
    string? ModeratableGameInstance = null,
    string? NoModerationGameWord = null,
    string? NoModerationGameInstance = null,
    string? NoPresenceGameWord = null,
    string? NoPresenceGameInstance = null,
    string? StoppedGameWord = null)
{
    public int RunningCount => Instances.Count(i => i.Running);
    public int StoppedCount => Instances.Count(i => !i.Running);

    public bool Has(FixtureRole role) => role switch
    {
        FixtureRole.UniqueGame => UniqueGameInstance is not null,
        FixtureRole.Running => RunningInstance is not null,
        FixtureRole.Stopped => StoppedInstance is not null,
        FixtureRole.AnyInstance => AnyInstance is not null,
        FixtureRole.NeverInstalledGame => NeverInstalledGame is not null,
        FixtureRole.MultipleInstances => Instances.Count >= 2,
        FixtureRole.ModeratableGame => ModeratableGameInstance is not null,
        FixtureRole.NoModerationGame => NoModerationGameInstance is not null,
        FixtureRole.NoPresenceGame => NoPresenceGameInstance is not null,
        _ => false,
    };

    /// <summary>The instance name a role points at, for trajectory checks ("did it act on the right server?").</summary>
    public string? InstanceFor(FixtureRole role) => role switch
    {
        FixtureRole.UniqueGame => UniqueGameInstance,
        FixtureRole.Running => RunningInstance,
        FixtureRole.Stopped => StoppedInstance,
        FixtureRole.AnyInstance => AnyInstance,
        FixtureRole.ModeratableGame => ModeratableGameInstance,
        FixtureRole.NoModerationGame => NoModerationGameInstance,
        FixtureRole.NoPresenceGame => NoPresenceGameInstance,
        _ => null,
    };

    /// <summary>The game type of a role's instance — used to fuzzy-match how the model referenced it.</summary>
    public string? GameFor(FixtureRole role)
    {
        var name = InstanceFor(role);
        return name is null ? null : Instances.FirstOrDefault(i => i.Name == name)?.Game;
    }

    /// <summary>Substitute prompt placeholders (<c>{unique_game}</c>, <c>{never_game}</c>) with resolved words.</summary>
    public string Fill(string prompt) => prompt
        .Replace("{unique_game}", UniqueGameWord ?? "the server")
        .Replace("{never_game}", NeverInstalledGame ?? "minecraft")
        .Replace("{moderatable_game}", ModeratableGameWord ?? "the server")
        .Replace("{no_moderation_game}", NoModerationGameWord ?? "the server")
        .Replace("{no_presence_game}", NoPresenceGameWord ?? "the server")
        .Replace("{stopped_game}", StoppedGameWord ?? "the server");
}

/// <summary>Resolves <see cref="FixtureRole"/>s from the live host and prints a loud preflight.</summary>
internal static class Fixtures
{
    // Preferred never-installed games, in order — recognizable to a reader, falling back to whatever
    // blueprint is uninstalled if none of these are available.
    private static readonly string[] PreferredAbsent = { "minecraft", "valheim", "terraria", "factorio", "satisfactory" };

    public static async Task<ResolvedFixtures> ResolveAsync(
        IServerInventory inventory, IServerOperations ops, IServerFacts? serverFacts, CancellationToken ct)
    {
        var instanceMap = await inventory.GetInstancesAsync(ct);
        var blueprints = (await inventory.GetBlueprintNamesAsync(ct))
            .OrderBy(b => b, StringComparer.OrdinalIgnoreCase).ToList();

        // Authoritative run-state per instance (is-active: native→watchdog). This is the field the
        // eval found to be CORRECT, unlike kgsm's `status` — so role resolution trusts it.
        var facts = new List<InstanceFact>();
        foreach (var (name, game) in instanceMap.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            var active = await ops.IsActiveAsync(name, ct);
            facts.Add(new InstanceFact(name, game, active.IsSuccess && active.Value));
        }

        // UniqueGame: a game type with exactly one instance; prefer a running one (so run-state cases
        // have a live target), then alphabetical by instance name — fully deterministic across reruns.
        var unique = facts
            .GroupBy(f => f.Game, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1)
            .Select(g => g.Single())
            .OrderByDescending(f => f.Running)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        var running = facts.Where(f => f.Running).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        var stopped = facts.Where(f => !f.Running).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        var any = facts.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();

        // Compare on normalized words: instance game types carry a ".bp" suffix ("minecraft.bp") but
        // blueprint names don't ("minecraft"), so a raw set-difference would treat an INSTALLED game as
        // absent. Normalize both sides before differencing.
        var installedWords = facts.Select(f => Word(f.Game)!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool NotInstalled(string blueprint) => !installedWords.Contains(Word(blueprint)!);
        var absent = PreferredAbsent.FirstOrDefault(g => blueprints.Contains(g, StringComparer.OrdinalIgnoreCase) && NotInstalled(g))
                     ?? blueprints.FirstOrDefault(NotInstalled);

        // Moderation support is the BLUEPRINT's fact — a game declares each command it has, and an
        // undeclared one is unsupported. Both halves of the moderation pair are drawn from unique-game
        // instances so a bare game word still resolves them, and both are read from live blueprint
        // detail rather than named here: which games can kick is a property of the host's catalog, not
        // of the corpus. A null detail is "couldn't read it", so it fills neither role.
        var uniqueGames = facts
            .GroupBy(f => f.Game, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1)
            .Select(g => g.Single())
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        InstanceFact? moderatable = null, unmoderatable = null;
        foreach (var f in uniqueGames)
        {
            if (moderatable is not null && unmoderatable is not null) break;
            var detail = await inventory.GetBlueprintDetailAsync(f.Game, ct);
            if (detail is null) continue;
            if (detail.ModerationVerbs.Count > 0) moderatable ??= f;
            else unmoderatable ??= f;
        }

        // Whether a roster is observable is measured, not assumed: the supervisor reports its
        // detection method per instance, and None means the game emits nothing to read. Asking the
        // real port is the only honest way to fill a role whose whole purpose is to distinguish
        // "measured nobody" from "cannot measure" — a hardcoded game name would be a guess about
        // exactly the fact under test. An unavailable reading fills the role with nothing.
        InstanceFact? unobservable = null;
        if (serverFacts is not null)
        {
            var presence = await serverFacts.GetPresenceAsync(ct);
            if (presence.State == FactsState.Available)
            {
                var blind = presence.Instances
                    .Where(p => p.Detection == PresenceDetection.None)
                    .Select(p => p.Instance)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                unobservable = uniqueGames.FirstOrDefault(f => blind.Contains(f.Name));
            }
        }

        return new ResolvedFixtures(
            facts, blueprints,
            UniqueGameWord: Word(unique?.Game),
            UniqueGameInstance: unique?.Name,
            RunningInstance: running?.Name,
            StoppedInstance: stopped?.Name,
            AnyInstance: any?.Name,
            NeverInstalledGame: Word(absent),
            ModeratableGameWord: Word(moderatable?.Game),
            ModeratableGameInstance: moderatable?.Name,
            NoModerationGameWord: Word(unmoderatable?.Game),
            NoModerationGameInstance: unmoderatable?.Name,
            NoPresenceGameWord: Word(unobservable?.Game),
            NoPresenceGameInstance: unobservable?.Name,
            StoppedGameWord: Word(stopped?.Game));
    }

    // kgsm blueprint/game types carry a ".bp" suffix (e.g. "factorio.bp"); strip it so the word a user
    // would actually speak ("factorio") goes into the prompt. The fuzzy instance match is unaffected.
    private static string? Word(string? game) =>
        game is null ? null
        : game.EndsWith(".bp", StringComparison.OrdinalIgnoreCase) ? game[..^3] : game;

    /// <summary>
    /// Print the resolved inventory and roles, and signal whether the run may proceed. Returns false
    /// on an empty inventory — the silent-empty-inventory failure that masquerades as real answers is
    /// the one trap this harness refuses to walk into.
    /// </summary>
    public static bool Preflight(ResolvedFixtures fx, TextWriter w)
    {
        w.WriteLine($"Inventory: {fx.Instances.Count} instance(s) ({fx.RunningCount} running, {fx.StoppedCount} stopped) · {fx.Blueprints.Count} blueprint(s)");

        if (fx.Instances.Count == 0)
        {
            w.WriteLine();
            w.WriteLine("ABORT: no instances visible to the assistant.");
            w.WriteLine("  The benchmark needs real servers to question. An empty inventory usually means");
            w.WriteLine("  XDG_DATA_HOME is overridden (kgsm's registry lives under ~/.local/share/kgsm),");
            w.WriteLine("  or KGSM:Path points at the wrong host. Fix that — do not trust an empty run.");
            return false;
        }

        foreach (var i in fx.Instances)
            w.WriteLine($"    · {i.Name}  ({i.Game})  {(i.Running ? "running" : "stopped")}");

        w.WriteLine("Resolved fixture roles:");
        Role(w, "unique_game", fx.UniqueGameInstance is null ? null
            : $"{fx.UniqueGameWord} → {fx.UniqueGameInstance}");
        Role(w, "running", fx.RunningInstance);
        Role(w, "stopped", fx.StoppedInstance);
        Role(w, "never_game", fx.NeverInstalledGame);
        Role(w, "moderatable", fx.ModeratableGameInstance is null ? null
            : $"{fx.ModeratableGameWord} → {fx.ModeratableGameInstance}");
        Role(w, "no_moderation", fx.NoModerationGameInstance is null ? null
            : $"{fx.NoModerationGameWord} → {fx.NoModerationGameInstance}");
        Role(w, "no_presence", fx.NoPresenceGameInstance is null ? null
            : $"{fx.NoPresenceGameWord} → {fx.NoPresenceGameInstance}");
        Role(w, "multiple", fx.Instances.Count >= 2 ? "yes" : null);
        return true;

        static void Role(TextWriter w, string name, string? value) =>
            w.WriteLine($"    {name,-13}{value ?? "— (unavailable — dependent cases will be skipped)"}");
    }
}
