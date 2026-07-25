namespace TheKrystalShip.Kgsm.Assistant.Ports;

/// <summary>
/// The evidence-driven repair step of <c>create_blueprint</c>: after a drafted blueprint test-installs
/// but fails to boot + listen, this reads the REAL install directory (the game's own shipped files and
/// launch scripts) and the boot LOGS, and proposes corrected launch fields for the next attempt. It
/// closes the gap the web-only research pass cannot: the executable's real name and location are knowable
/// by looking at what the install actually put on disk, and WHY a boot failed is knowable from the
/// server's own log output — both far higher-fidelity signals than any web page. The division of labour
/// mirrors the rest of the pipeline: research selects sources, synthesis extracts from them, and repair
/// grounds a correction in the running environment — the empirical boot + listen check stays the backstop.
/// <para>
/// Returns <see langword="null"/> when it has no better idea than the current draft, so the caller stops
/// re-running a draft that will not improve rather than burning the remaining attempts on an identical
/// config. MUST NOT throw — a failure is a null result (the pipeline then treats the attempt as
/// unrepairable and stops).
/// </para>
/// </summary>
public interface IBlueprintRepair
{
    /// <summary>Proposes corrected launch fields from the install evidence + boot log, or
    /// <see langword="null"/> to stop (no improvement possible). MUST NOT throw.</summary>
    Task<BlueprintRepairProposal?> RepairAsync(
        BlueprintRepairContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Everything the repair step gets to reason over for one failed attempt: the launch fields that were
/// just tried, plus the two ground-truth evidence sources the web research pass never had — the real
/// installed <see cref="InstallTree"/> (what SteamCMD/the download actually placed on disk) and any shipped
/// <see cref="LaunchScripts"/> (the game's own wrapper scripts, which name the true binary + its args), and
/// the <see cref="BootLog"/> tail from the attempt (why it did not come up). All neutral strings — the
/// port carries no kgsm-lib types so the domain stays decoupled from the write-side authorities.
/// </summary>
/// <param name="Game">The game's display name (for the model's grounding).</param>
/// <param name="ExecutableFile">The <c>executable_file</c> just tried (relative to the install subdir).</param>
/// <param name="ExecutableSubdirectory">The <c>executable_subdirectory</c> just tried (relative to the install root), or empty.</param>
/// <param name="ExecutableArguments">The <c>executable_arguments</c> just tried, or empty.</param>
/// <param name="StartupSuccessRegex">The <c>startup_success_regex</c> just tried, or empty.</param>
/// <param name="Ports">The port(s) just tried, in UFW form, or empty.</param>
/// <param name="InstallSucceeded">Whether the test-install itself completed (vs. the download/setup failing).</param>
/// <param name="InstallError">The install stderr when <paramref name="InstallSucceeded"/> is false; else null.</param>
/// <param name="InstallTree">A bounded, rendered listing of the real install directory (paths relative to
/// the install root — the same root <paramref name="ExecutableSubdirectory"/> is relative to). Empty if the
/// install produced no files to read.</param>
/// <param name="LaunchScripts">The concatenated text of the shipped <c>*.sh</c> launch scripts found in the
/// install tree (bounded), or empty if none were found. The single highest-value evidence — a wrapper
/// script names the true binary and the exact args and sets the runtime env the raw binary needs.</param>
/// <param name="BootLog">The recent server log lines from the failed attempt (a tail), joined; empty if the
/// server produced no log (e.g. the executable path did not exist so nothing ran).</param>
/// <param name="PortsReachable">Whether the configured ports were bound during the attempt: true (bound but
/// some other readiness signal missing), false (running but never bound), or null (never probed — e.g. the
/// process was not running).</param>
public sealed record BlueprintRepairContext(
    string Game,
    string ExecutableFile,
    string ExecutableSubdirectory,
    string ExecutableArguments,
    string StartupSuccessRegex,
    string Ports,
    bool InstallSucceeded,
    string? InstallError,
    string InstallTree,
    string LaunchScripts,
    string BootLog,
    bool? PortsReachable);

/// <summary>
/// A proposed correction to a draft's LAUNCH fields — only the fields the install evidence + boot log can
/// actually inform. Every field is nullable: <see langword="null"/> means "leave the current draft's value
/// unchanged" (the caller merges non-null fields over the current draft). The Steam app ids are
/// deliberately absent — those are not discoverable from the install tree and were already confirmed by the
/// fact the download succeeded, so repair never touches them.
/// </summary>
/// <param name="ExecutableFile">Corrected <c>executable_file</c> (the true binary/script filename, or an
/// interpreter like <c>java</c>/<c>dotnet</c>), or null to keep the current one.</param>
/// <param name="ExecutableSubdirectory">Corrected <c>executable_subdirectory</c> relative to the install
/// root (empty string to clear it — the binary sits at the root), or null to keep the current one.</param>
/// <param name="ExecutableArguments">Corrected <c>executable_arguments</c> (empty string to clear them),
/// or null to keep the current ones.</param>
/// <param name="StartupSuccessRegex">Corrected <c>startup_success_regex</c> (a ready line actually seen in
/// the boot log), or null to keep the current one.</param>
/// <param name="Ports">Corrected primary port NUMBER (the caller renders it to UFW form), or null to keep
/// the current one.</param>
public sealed record BlueprintRepairProposal(
    string? ExecutableFile,
    string? ExecutableSubdirectory,
    string? ExecutableArguments,
    string? StartupSuccessRegex,
    string? Ports);

/// <summary>
/// Default <see cref="IBlueprintRepair"/> for hosts with no model wired for repair: always returns
/// <see langword="null"/> so the pipeline behaves exactly as it did without a repair step (one attempt, no
/// evidence-driven correction). Registered with <c>TryAddSingleton</c>; a host with an LLM registers the
/// real repairer afterward, and that later registration is the one resolved.
/// </summary>
internal sealed class DisabledBlueprintRepair : IBlueprintRepair
{
    public Task<BlueprintRepairProposal?> RepairAsync(
        BlueprintRepairContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult<BlueprintRepairProposal?>(null);
}
