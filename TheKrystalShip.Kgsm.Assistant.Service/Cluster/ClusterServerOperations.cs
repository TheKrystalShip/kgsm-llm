using System.Net.Http.Json;
using System.Text.Json;

using TheKrystalShip.Api.Contracts;

// Two names this service already uses for something else: its own chat-command request, and the
// generic host builder. Spelled out here so a call binds to the node's contract rather than to
// whichever one the surrounding namespace happens to win with.
using ApiCommandRequest = TheKrystalShip.Api.Contracts.CommandRequest;
using ApiHost = TheKrystalShip.Api.Contracts.Host;

using TheKrystalShip.Kgsm.Assistant.Files;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Service.Cluster;

/// <summary>
/// Acting on a server that is on somebody else's machine.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every call is routed by the server's own id.</b> The id is what a person says and what a tool
/// takes; which machine holds it is this class's problem and appears nowhere above it. A server no
/// node reported, or one two nodes reported, is refused with the reason rather than sent somewhere on
/// a guess.
/// </para>
/// <para>
/// <b>A lifecycle command is accepted and then watched.</b> The node answers <c>202</c> with a job and
/// settles it later, so the outcome is read off the job rather than inferred from the server's
/// run-state — a start the engine refused leaves the server stopped, which is indistinguishable from a
/// start nobody issued, and the reason lives only on the job.
/// </para>
/// <para>
/// <b>A file edit is resolved here and written there.</b> The file is read over the API and the
/// replacement is applied to the bytes that came back, by the same code the standalone standing uses —
/// so an anchor that matches twice is refused identically whichever engine the file is on.
/// </para>
/// </remarks>
internal sealed class ClusterServerOperations(
    ClusterServers servers,
    NodeApiClient api,
    ILogger<ClusterServerOperations> logger) : IServerOperations
{
    /// <summary>
    /// How long a command is watched before its outcome is reported as not yet known.
    /// </summary>
    /// <remarks>
    /// Generous because an update downloads a game, and bounded because "still going" has to become an
    /// answer at some point. Reaching it is not a failure and is not reported as one: what is known is
    /// that the node took the command and had not finished, which is a different sentence from either
    /// success or refusal.
    /// </remarks>
    private static readonly TimeSpan Settle = TimeSpan.FromMinutes(10);

    /// <summary>How often the job is asked about while it settles.</summary>
    private static readonly TimeSpan Beat = TimeSpan.FromSeconds(2);

    /// <summary>What every write declares itself as, so a node's audit records where it came from.</summary>
    private const string Origin = "assistant";

    // ---- lifecycle -------------------------------------------------------------------------------

    public Task<Result> StartAsync(string instance, CancellationToken cancellationToken = default) =>
        CommandAsync(instance, CommandVerb.Start, cancellationToken);

    public Task<Result> StopAsync(string instance, CancellationToken cancellationToken = default) =>
        CommandAsync(instance, CommandVerb.Stop, cancellationToken);

    public Task<Result> RestartAsync(string instance, CancellationToken cancellationToken = default) =>
        CommandAsync(instance, CommandVerb.Restart, cancellationToken);

    public Task<Result> UpdateAsync(string instance, CancellationToken cancellationToken = default) =>
        CommandAsync(instance, CommandVerb.Update, cancellationToken);

    private async Task<Result> CommandAsync(string instance, string verb, CancellationToken ct)
    {
        (ClusterNode? node, string? failure) = await servers.RouteAsync(instance, ct).ConfigureAwait(false);
        if (node is null)
            return Result.Failure(failure!);

        NodeResult<CommandAccepted> accepted = await api.SendAsync(
            HttpMethod.Post, node, $"/api/v1/servers/{Segment(instance)}/commands",
            Json(new ApiCommandRequest(verb, Origin), ApiContractsJson.Default.CommandRequest),
            ApiContractsJson.Default.CommandAccepted, ct).ConfigureAwait(false);

        if (!accepted.Answered)
            return Result.Failure($"{node.MemberId} {accepted.Failure}.");

        if (accepted.Body?.Job is not { } job)
            return Result.Failure($"{node.MemberId} accepted the {verb} but named no job to follow.");

        servers.Forget();
        return await WatchAsync(node, job, verb, instance, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Follow a job to its end, and report what the node said happened.
    /// </summary>
    /// <remarks>
    /// A job that stops being readable is reported as unknown rather than as failed. The node has it
    /// and this member does not, so "it did not work" would be a claim about somebody else's machine
    /// made from the fact that a request failed here.
    /// </remarks>
    private async Task<Result> WatchAsync(
        ClusterNode node, Job job, string verb, string instance, CancellationToken ct)
    {
        DateTimeOffset until = DateTimeOffset.UtcNow + Settle;

        while (DateTimeOffset.UtcNow < until)
        {
            await Task.Delay(Beat, ct).ConfigureAwait(false);

            NodeResult<Job> read = await api
                .GetAsync(node, $"/api/v1/jobs/{Segment(job.Id)}", ApiContractsJson.Default.Job, ct)
                .ConfigureAwait(false);

            if (!read.Answered || read.Body is not { } current)
            {
                logger.LogWarning(
                    "could not read job {Job} on {Node}: {Failure}", job.Id, node.MemberId, read.Failure);
                return Result.Failure(
                    $"{node.MemberId} took the {verb} of '{instance}' and then could not be asked how it went. "
                    + "It may have worked; ask again rather than repeating it.");
            }

            switch (current.State)
            {
                case JobState.Succeeded:
                    servers.Forget();
                    return Result.Success();

                case JobState.Failed:
                    servers.Forget();
                    return Result.Failure(string.IsNullOrWhiteSpace(current.Error)
                        ? $"{node.MemberId} could not {verb} '{instance}' and gave no reason."
                        : current.Error!.Trim());
            }
        }

        servers.Forget();
        return Result.Failure(
            $"{node.MemberId} is still working on the {verb} of '{instance}' after {Settle.TotalMinutes:0} "
            + "minutes. It has not failed — ask again rather than repeating it.");
    }

    // ---- reads -----------------------------------------------------------------------------------

    public async Task<Result<string>> GetStatusAsync(string instance, CancellationToken cancellationToken = default)
    {
        (ClusterNode? node, string? failure) = await servers.RouteAsync(instance, cancellationToken).ConfigureAwait(false);
        if (node is null)
            return Result.Failure<string>(failure!);

        NodeResult<Server> read = await api.GetAsync(
            node, $"/api/v1/servers/{Segment(instance)}", ApiContractsJson.Default.Server, cancellationToken)
            .ConfigureAwait(false);

        if (!read.Answered || read.Body is not { } server)
            return Result.Failure<string>($"{node.MemberId} {read.Failure ?? "answered nothing"}.");

        return Result.Success($"{server.Id} is {server.Status} on {node.MemberId}");
    }

    /// <summary>
    /// Whether one server is running, from the same reading a status read gives.
    /// </summary>
    /// <remarks>
    /// A status the node reports as unknown is a failure here rather than a false: not knowing whether
    /// something runs is not the same as knowing it does not, and a caller branching on a bool would
    /// treat the two identically.
    /// </remarks>
    public async Task<Result<bool>> IsActiveAsync(string instance, CancellationToken cancellationToken = default)
    {
        (ClusterNode? node, string? failure) = await servers.RouteAsync(instance, cancellationToken).ConfigureAwait(false);
        if (node is null)
            return Result.Failure<bool>(failure!);

        NodeResult<Server> read = await api.GetAsync(
            node, $"/api/v1/servers/{Segment(instance)}", ApiContractsJson.Default.Server, cancellationToken)
            .ConfigureAwait(false);

        if (!read.Answered || read.Body is not { } server)
            return Result.Failure<bool>($"{node.MemberId} {read.Failure ?? "answered nothing"}.");

        return string.Equals(server.Status, ServerStatus.Unknown, StringComparison.Ordinal)
            ? Result.Failure<bool>($"{node.MemberId} cannot read whether '{instance}' is running.")
            : Result.Success(!string.Equals(server.Status, ServerStatus.Stopped, StringComparison.Ordinal));
    }

    /// <summary>
    /// Every server in the cluster with whether it is running — one read per node rather than one per
    /// server, which is what keeps "which servers are running?" a single answer.
    /// </summary>
    /// <remarks>
    /// A node that did not answer contributes no entries, and the servers it holds are simply not in
    /// the list. That is the one thing this shape cannot say — it is keyed by server and an unreachable
    /// node has no server to hang the reason on — so the unreached nodes are reported through the
    /// inventory, which is keyed by nothing and can.
    /// </remarks>
    public async Task<Result<IReadOnlyList<FleetStatusEntry>>> GetFleetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        FleetServers fleet = await servers.ReadAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success<IReadOnlyList<FleetStatusEntry>>(
        [
            .. fleet.Found.Select(p => string.Equals(p.Server.Status, ServerStatus.Unknown, StringComparison.Ordinal)
                ? new FleetStatusEntry(p.Server.Id, FleetStatusAvailability.Unavailable, null,
                    $"{p.Node} cannot read this server's state")
                : new FleetStatusEntry(p.Server.Id, FleetStatusAvailability.Read,
                    !string.Equals(p.Server.Status, ServerStatus.Stopped, StringComparison.Ordinal), null)),
        ]);
    }

    /// <summary>
    /// The inputs a health check is judged from, gathered from the node the server is on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Composed here rather than asked for as one route, which is the decision the fleet design rests
    /// on: a health endpoint would freeze one node's idea of what healthy means into a wire contract
    /// that then has to move on both sides together. The judgment stays in the aggregator, and this
    /// only fetches and maps.
    /// </para>
    /// <para>
    /// <b>Two inputs are absent over the API and say so rather than being invented.</b> The previous
    /// run's ending is not a read a node serves — its console surface holds the current run — so the
    /// stability check has nothing to judge and skips, which is what it does for a server with one run
    /// on record. And the port probe is host-local: whether a port is bound is something the machine
    /// running the server measures, so the reason travels in place of a reading and the check skips
    /// rather than reporting ports that were never looked at.
    /// </para>
    /// </remarks>
    public async Task<Result<InstanceHealthSnapshot>> GetHealthSnapshotAsync(
        string instance, CancellationToken cancellationToken = default)
    {
        (ClusterNode? node, string? failure) = await servers.RouteAsync(instance, cancellationToken).ConfigureAwait(false);
        if (node is null)
            return Result.Failure<InstanceHealthSnapshot>(failure!);

        NodeResult<Server> read = await api.GetAsync(
            node, $"/api/v1/servers/{Segment(instance)}", ApiContractsJson.Default.Server, cancellationToken)
            .ConfigureAwait(false);

        if (!read.Answered || read.Body is not { } server)
        {
            return Result.Failure<InstanceHealthSnapshot>(
                $"{node.MemberId} {read.Failure ?? "answered nothing"} about '{instance}'.");
        }

        NodeResult<ConsoleScrollback> console = await api.GetAsync(
            node, $"/api/v1/servers/{Segment(instance)}/console?tail={LogSample}",
            ApiContractsJson.Default.ConsoleScrollback, cancellationToken).ConfigureAwait(false);

        (HostDisk? disk, string? diskReason) = await ReadHostDiskAsync(node, cancellationToken).ConfigureAwait(false);

        return Result.Success(new InstanceHealthSnapshot(
            Running: string.Equals(server.Status, ServerStatus.Unknown, StringComparison.Ordinal)
                ? null
                : !string.Equals(server.Status, ServerStatus.Stopped, StringComparison.Ordinal),
            RecentLogLines: console.Body?.Lines ?? [],
            RecentLogLinesRequested: LogSample,
            UpdatesAvailable: server.UpdateAvailable,
            CurrentVersion: string.IsNullOrWhiteSpace(server.Version) ? null : server.Version,
            LatestVersion: server.LatestVersion,
            HostDisk: disk,
            HostDiskUnavailableReason: diskReason,
            PortsReachable: null,
            PortsDetail: $"not probed: whether a port is bound is measured on {node.MemberId} itself",
            Restart: null,
            LibraryState: LibraryStateOf(server.LibraryState)));
    }

    /// <summary>How many trailing console lines a health check asks for. What it ASKED for is what the
    /// aggregator judges the sample against, so it travels with the lines.</summary>
    private const int LogSample = 200;

    /// <summary>
    /// The node's root filesystem, as a percentage and two human figures.
    /// </summary>
    /// <remarks>
    /// The root mount, because that is the disk a health check is about: an instance's own library may
    /// sit elsewhere, and reporting whichever mount happened to be listed first would attribute one
    /// disk's fullness to another. A node reporting no mounts leaves the check to skip.
    /// </remarks>
    private async Task<(HostDisk?, string?)> ReadHostDiskAsync(ClusterNode node, CancellationToken ct)
    {
        NodeResult<ApiHost> read = await api
            .GetAsync(node, $"/api/v1/hosts/{Segment(node.MemberId)}", ApiContractsJson.Default.Host, ct)
            .ConfigureAwait(false);

        if (!read.Answered || read.Body is not { } host)
            return (null, $"{node.MemberId} could not be asked about its disk");

        DiskCapacity? root = host.Disks?.FirstOrDefault(d => d.Mount == "/");
        if (root is null || root.Total <= 0)
            return (null, $"{node.MemberId} reported no root filesystem");

        return (new HostDisk(
            UsedPercent: (int)Math.Round(root.Used / root.Total * 100),
            Size: $"{root.Total:0.#}G",
            Available: $"{root.Total - root.Used:0.#}G",
            Mount: root.Mount), null);
    }

    /// <summary>Where the server's files stand, as the node reports it. An unrecognised word is left
    /// unknown rather than mapped to the nearest thing it resembles.</summary>
    private static ServerLibraryState? LibraryStateOf(string? reported) => reported?.ToLowerInvariant() switch
    {
        "online" => ServerLibraryState.Online,
        "offline" => ServerLibraryState.Offline,
        "unregistered" => ServerLibraryState.Unregistered,
        _ => null,
    };

    // ---- an instance's files ---------------------------------------------------------------------

    public async Task<Result<string>> ReadInstanceFileAsync(
        string instance, string relativePath, CancellationToken cancellationToken = default)
    {
        (ClusterNode? node, string? failure) = await servers.RouteAsync(instance, cancellationToken).ConfigureAwait(false);
        if (node is null)
            return Result.Failure<string>(failure!);

        NodeResult<FileContentDto> read = await api.GetAsync(
            node, $"/api/v1/servers/{Segment(instance)}/files/content?path={Segment(relativePath)}",
            ApiContractsJson.Default.FileContentDto, cancellationToken).ConfigureAwait(false);

        return read.Answered && read.Body is { } file
            ? Result.Success(file.Content)
            : Result.Failure<string>(read.Failure ?? $"{node.MemberId} could not read '{relativePath}'.");
    }

    public async Task<Result<IReadOnlyList<InstanceDirEntry>>> ListInstanceDirectoryAsync(
        string instance, string? relativeSubdir = null, CancellationToken cancellationToken = default)
    {
        (ClusterNode? node, string? failure) = await servers.RouteAsync(instance, cancellationToken).ConfigureAwait(false);
        if (node is null)
            return Result.Failure<IReadOnlyList<InstanceDirEntry>>(failure!);

        NodeResult<DirListingDto> read = await api.GetAsync(
            node, $"/api/v1/servers/{Segment(instance)}/files?path={Segment(relativeSubdir ?? string.Empty)}",
            ApiContractsJson.Default.DirListingDto, cancellationToken).ConfigureAwait(false);

        if (!read.Answered || read.Body is not { } listing)
            return Result.Failure<IReadOnlyList<InstanceDirEntry>>(
                read.Failure ?? $"{node.MemberId} could not list that directory.");

        return Result.Success<IReadOnlyList<InstanceDirEntry>>(
        [
            .. listing.Entries.Select(e => new InstanceDirEntry(
                e.Name,
                string.Equals(e.Kind, "dir", StringComparison.Ordinal),
                e.SizeBytes ?? 0,
                e.Mtime)),
        ]);
    }

    public async Task<Result<InstanceFileMatches>> FindInstanceFilesAsync(
        string instance, string pattern, string? relativeSubdir = null,
        CancellationToken cancellationToken = default)
    {
        (ClusterNode? node, string? failure) = await servers.RouteAsync(instance, cancellationToken).ConfigureAwait(false);
        if (node is null)
            return Result.Failure<InstanceFileMatches>(failure!);

        NodeResult<FileFindDto> read = await api.GetAsync(
            node,
            $"/api/v1/servers/{Segment(instance)}/files/find"
            + $"?path={Segment(relativeSubdir ?? string.Empty)}&pattern={Segment(pattern)}",
            ApiContractsJson.Default.FileFindDto, cancellationToken).ConfigureAwait(false);

        if (!read.Answered || read.Body is not { } found)
            return Result.Failure<InstanceFileMatches>(read.Failure ?? $"{node.MemberId} could not search there.");

        return Result.Success(new InstanceFileMatches(
            [.. found.Matches.Select(m => m.Name)], found.Truncated, found.Incomplete));
    }

    public async Task<Result<InstanceContentMatches>> SearchInstanceFilesAsync(
        string instance, string pattern, string? relativeSubdir = null, bool ignoreCase = true,
        CancellationToken cancellationToken = default)
    {
        (ClusterNode? node, string? failure) = await servers.RouteAsync(instance, cancellationToken).ConfigureAwait(false);
        if (node is null)
            return Result.Failure<InstanceContentMatches>(failure!);

        NodeResult<FileSearchDto> read = await api.GetAsync(
            node,
            $"/api/v1/servers/{Segment(instance)}/files/search"
            + $"?path={Segment(relativeSubdir ?? string.Empty)}&pattern={Segment(pattern)}"
            + $"&ignoreCase={(ignoreCase ? "true" : "false")}",
            ApiContractsJson.Default.FileSearchDto, cancellationToken).ConfigureAwait(false);

        if (!read.Answered || read.Body is not { } found)
            return Result.Failure<InstanceContentMatches>(read.Failure ?? $"{node.MemberId} could not search there.");

        return Result.Success(new InstanceContentMatches(
            [.. found.Hits.Select(h => new InstanceContentMatch(h.Path, h.Line, h.Text))],
            found.Truncated, found.Incomplete));
    }

    public async Task<Result> WriteInstanceFileAsync(
        string instance, string relativePath, string content, CancellationToken cancellationToken = default)
    {
        (ClusterNode? node, string? failure) = await servers.RouteAsync(instance, cancellationToken).ConfigureAwait(false);
        if (node is null)
            return Result.Failure(failure!);

        // No etag: the content was resolved from a read this member made moments ago, and the node
        // refuses a write to a file that is not already there. Sending an etag would turn a file
        // somebody else touched in between into a refusal a person can do nothing about from a chat.
        NodeResult<NodeNothing> wrote = await api.SendAsync(
            HttpMethod.Put,
            node, $"/api/v1/servers/{Segment(instance)}/files/content?path={Segment(relativePath)}",
            Json(new SaveFileRequest(content, null, Origin), ApiContractsJson.Default.SaveFileRequest),
            cancellationToken).ConfigureAwait(false);

        return wrote.Answered
            ? Result.Success()
            : Result.Failure($"{node.MemberId} would not write '{relativePath}': {wrote.Failure}");
    }

    /// <summary>
    /// Resolve an anchored replacement against the file as it is now.
    /// </summary>
    /// <remarks>
    /// The file is read over the API and the replacement is applied to those bytes by the same code the
    /// standalone standing runs, so an anchor matching twice is refused in the same words whichever
    /// machine the file is on. Nothing is written here: this is the read half of a staged write.
    /// </remarks>
    public async Task<Result<string>> PrepareInstanceFileEditAsync(
        string instance, string relativePath, string oldText, string newText,
        string? copyFromPath = null, CancellationToken cancellationToken = default)
    {
        bool seeded = !string.IsNullOrWhiteSpace(copyFromPath);
        string sourcePath = seeded ? copyFromPath!.Trim() : relativePath;

        Result<string> source = await ReadInstanceFileAsync(instance, sourcePath, cancellationToken)
            .ConfigureAwait(false);
        if (!source.IsSuccess)
            return Result.Failure<string>(source.Error!);

        // An empty anchor is a copy, and only a seeded write can mean one: there is no text to replace,
        // so the reference file's content IS the proposal.
        if (oldText.Length == 0)
        {
            return seeded
                ? Result.Success(source.Value!)
                : Result.Failure<string>(
                    "no text to replace was given. Pass old_string exactly as the file read showed it.");
        }

        FileEditResult edit = FileEdit.Apply(source.Value!, oldText, newText);
        return edit.Outcome switch
        {
            FileEditOutcome.Applied => Result.Success(edit.Content!),
            FileEditOutcome.NoMatch => Result.Failure<string>(
                $"that text is not in '{sourcePath}'. Read the file and copy the line exactly."),
            FileEditOutcome.Ambiguous => Result.Failure<string>(
                $"that text appears {edit.Matches} times in '{sourcePath}', so which one was meant is "
                + "unknown. Include a surrounding line to make it unique."),
            FileEditOutcome.NoChange => Result.Failure<string>(
                "the replacement is identical to the text it replaces, so the edit changes nothing."),
            _ => Result.Failure<string>("no text to replace was given."),
        };
    }

    /// <inheritdoc cref="PrepareInstanceFileEditAsync"/>
    public async Task<Result<SettingEditSummary>> PrepareInstanceSettingEditAsync(
        string instance, string relativePath, string settingKey, string settingValue,
        string? copyFromPath = null, CancellationToken cancellationToken = default)
    {
        bool seeded = !string.IsNullOrWhiteSpace(copyFromPath);
        string sourcePath = seeded ? copyFromPath!.Trim() : relativePath;

        Result<string> source = await ReadInstanceFileAsync(instance, sourcePath, cancellationToken)
            .ConfigureAwait(false);
        if (!source.IsSuccess)
            return Result.Failure<SettingEditSummary>(source.Error!);

        SettingEditResult edit = SettingEdit.Apply(source.Value!, settingKey, settingValue);
        return edit.Outcome switch
        {
            SettingEditOutcome.Applied => Result.Success(
                new SettingEditSummary(edit.Content!, edit.PreviousValue!, settingValue)),
            SettingEditOutcome.NoMatch when source.Value!.Trim().Length == 0 =>
                Result.Failure<SettingEditSummary>(
                    $"'{sourcePath}' is empty, so it holds no settings to change. Pass the game's "
                    + "default or reference file as copy_from to fill it in."),
            SettingEditOutcome.NoMatch => Result.Failure<SettingEditSummary>(
                $"'{sourcePath}' has no setting called '{settingKey}'. Search the instance's files for "
                + "it to find which file carries it."),
            SettingEditOutcome.Ambiguous => Result.Failure<SettingEditSummary>(
                $"'{settingKey}' appears {edit.Matches} times in '{sourcePath}', so which value was "
                + "meant is unknown."),
            _ => Result.Failure<SettingEditSummary>($"'{settingKey}' is not a setting name."),
        };
    }

    // ---- backups ---------------------------------------------------------------------------------

    public Task<Result> CreateBackupAsync(string instance, CancellationToken cancellationToken = default) =>
        AwaitedAsync(instance, HttpMethod.Post, "/backups",
            Json(new CreateBackupRequest(Origin), ApiContractsJson.Default.CreateBackupRequest),
            "back up", cancellationToken);

    public Task<Result> RestoreBackupAsync(
        string instance, string backupId, CancellationToken cancellationToken = default) =>
        AwaitedAsync(instance, HttpMethod.Post, "/backups/restore",
            Json(new RestoreBackupRequest(backupId, Origin), ApiContractsJson.Default.RestoreBackupRequest),
            "restore a backup onto", cancellationToken);

    /// <summary>
    /// Delete one backup. Answers inside the request rather than through a job, which is the node's
    /// own shape for it: it holds the server's slot as a mutex and settles before replying.
    /// </summary>
    public Task<Result> DeleteBackupAsync(
        string instance, string backupId, CancellationToken cancellationToken = default) =>
        WriteAsync(instance, HttpMethod.Delete,
            $"/backups/{Segment(backupId)}?origin={Origin}", null,
            $"delete the backup '{backupId}' of", cancellationToken);

    public Task<Result> PruneBackupsAsync(
        string instance, int keep, CancellationToken cancellationToken = default) =>
        keep < 1
            ? Task.FromResult(Result.Failure("A prune must keep at least one backup."))
            : WriteAsync(instance, HttpMethod.Post, "/backups/prune",
                Json(new PruneBackupsRequest(keep, Origin), ApiContractsJson.Default.PruneBackupsRequest),
                "prune the backups of", cancellationToken);

    // ---- configuration and settings ---------------------------------------------------------------

    public Task<Result> SetInstanceConfigValueAsync(
        string instance, string key, string value, CancellationToken cancellationToken = default) =>
        WriteAsync(instance, HttpMethod.Patch, "/config",
            Json(new ServerConfigPatch(new Dictionary<string, string> { [key] = value }, Origin),
                 ApiContractsJson.Default.ServerConfigPatch),
            $"set '{key}' on", cancellationToken);

    /// <summary>
    /// Set whether the server starts when its machine boots. The supervisor's persisted intent, not a
    /// lifecycle action — it settles against no run-state because there is none to observe.
    /// </summary>
    public Task<Result> SetAutostartAsync(
        string instance, bool enabled, CancellationToken cancellationToken = default) =>
        WriteAsync(instance, HttpMethod.Patch, "/settings",
            Json(new ServerSettingsPatch(
                    AutoUpdate: null, Autostart: enabled, CpuPriority: null, MemoryCapMb: null,
                    MaintenanceWindows: null, Timezone: null, BackupRetention: null, Origin: Origin),
                 ApiContractsJson.Default.ServerSettingsPatch),
            enabled ? "enable boot autostart for" : "disable boot autostart for", cancellationToken);

    // ---- installing and removing -------------------------------------------------------------------

    /// <summary>
    /// Install a game somewhere in the cluster.
    /// </summary>
    /// <remarks>
    /// <b>A new server has no node yet, so one is chosen rather than routed to.</b> There is nothing to
    /// look up — the id does not exist — and the cluster has no placement policy, so this goes to the
    /// only node that offers the game when exactly one does, and refuses when several do. Refusing is
    /// the honest answer to a question nobody has been asked: which machine should this run on.
    /// </remarks>
    public async Task<Result> InstallAsync(
        string blueprint,
        string? instanceName,
        CancellationToken cancellationToken = default,
        string? version = null,
        int? port = null,
        string? library = null)
    {
        (ClusterNode? node, string? failure) =
            await servers.RouteForInstallAsync(blueprint, cancellationToken).ConfigureAwait(false);
        if (node is null)
            return Result.Failure(failure!);

        NodeResult<CommandAccepted> accepted = await api.SendAsync(
            HttpMethod.Post, node, "/api/v1/servers",
            Json(new InstallRequest(
                    Blueprint: blueprint, Name: instanceName, Origin: Origin,
                    Port: port, Library: library, Version: version),
                 ApiContractsJson.Default.InstallRequest),
            ApiContractsJson.Default.CommandAccepted, cancellationToken).ConfigureAwait(false);

        if (!accepted.Answered)
            return Result.Failure($"{node.MemberId} would not install {blueprint}: {accepted.Failure}");

        if (accepted.Body?.Job is not { } job)
            return Result.Failure($"{node.MemberId} accepted the install but named no job to follow.");

        servers.Forget();
        return await WatchAsync(node, job, "install", instanceName ?? blueprint, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Result> UninstallAsync(string instance, CancellationToken cancellationToken = default)
    {
        (ClusterNode? node, string? failure) = await servers.RouteAsync(instance, cancellationToken).ConfigureAwait(false);
        if (node is null)
            return Result.Failure(failure!);

        NodeResult<CommandAccepted> accepted = await api.SendAsync(
            HttpMethod.Delete, node, $"/api/v1/servers/{Segment(instance)}?origin={Origin}", null,
            ApiContractsJson.Default.CommandAccepted, cancellationToken).ConfigureAwait(false);

        if (!accepted.Answered)
            return Result.Failure($"{node.MemberId} would not uninstall '{instance}': {accepted.Failure}");

        servers.Forget();

        return accepted.Body?.Job is { } job
            ? await WatchAsync(node, job, "uninstall", instance, cancellationToken).ConfigureAwait(false)
            : Result.Success();
    }

    // ---- moderation --------------------------------------------------------------------------------

    public Task<Result> KickPlayerAsync(
        string instance, string target, CancellationToken cancellationToken = default) =>
        ModerateAsync(instance, target, ModerationAction.Kick, cancellationToken);

    public Task<Result> BanPlayerAsync(
        string instance, string target, CancellationToken cancellationToken = default) =>
        ModerateAsync(instance, target, ModerationAction.Ban, cancellationToken);

    public Task<Result> UnbanPlayerAsync(
        string instance, string target, CancellationToken cancellationToken = default) =>
        ModerateAsync(instance, target, ModerationAction.Unban, cancellationToken);

    /// <summary>
    /// Act on one player, named against the server's own roster.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The target is resolved, not forwarded.</b> A node takes a roster key and reads every identity
    /// field off its own record for it, so nothing here — and nothing the model wrote — decides who
    /// gets banned. Which field the game wants is the blueprint's own declaration and the node applies
    /// it, so a name is never sent where an account id was asked for.
    /// </para>
    /// <para>
    /// The consequence worth stating: somebody the server has never seen cannot be acted on, because
    /// there is no record to resolve. That is a real narrowing and it is the right one — the alternative
    /// is a chat turn that can ban an arbitrary string.
    /// </para>
    /// </remarks>
    private async Task<Result> ModerateAsync(
        string instance, string target, string action, CancellationToken ct)
    {
        (ClusterNode? node, string? failure) = await servers.RouteAsync(instance, ct).ConfigureAwait(false);
        if (node is null)
            return Result.Failure(failure!);

        NodeResult<PlayersResponse> roster = await api.GetAsync(
            node, $"/api/v1/servers/{Segment(instance)}/players",
            ApiContractsJson.Default.PlayersResponse, ct).ConfigureAwait(false);

        if (!roster.Answered || roster.Body is not { } players)
            return Result.Failure($"{node.MemberId} would not answer who is on '{instance}': {roster.Failure}");

        RosterPlayer? found = players.Players.FirstOrDefault(p =>
            Same(p.PlayerName, target) || Same(p.PlayerId, target) || Same(p.PlayerIdentity, target));

        if (found is null)
        {
            return Result.Failure(players.Players.Count == 0
                ? $"'{instance}' has no record of any player, so there is nobody to {action}."
                : $"'{instance}' has no record of a player called '{target}', so there is nobody to "
                  + $"{action}. Nothing here will act on a name the server has never seen.");
        }

        // The driving surface rides the query string: the route takes no body, so this is where an
        // audit row learns the action came from a chat rather than from the panel.
        NodeResult<ModerationResult> done = await api.SendAsync(
            HttpMethod.Post, node,
            $"/api/v1/servers/{Segment(instance)}/players/{Segment(found.PlayerIdentity)}/{action}"
            + $"?origin={Origin}",
            null, ApiContractsJson.Default.ModerationResult, ct).ConfigureAwait(false);

        return done.Answered
            ? Result.Success()
            : Result.Failure($"{node.MemberId} would not {action} that player: {done.Failure}");
    }

    private static bool Same(string? a, string b) =>
        !string.IsNullOrWhiteSpace(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ---- helpers ---------------------------------------------------------------------------------

    /// <summary>A write that the node settles inside the request and answers with nothing.</summary>
    private async Task<Result> WriteAsync(
        string instance, HttpMethod method, string suffix, string? json, string verb, CancellationToken ct)
    {
        (ClusterNode? node, string? failure) = await servers.RouteAsync(instance, ct).ConfigureAwait(false);
        if (node is null)
            return Result.Failure(failure!);

        NodeResult<NodeNothing> sent = await api.SendAsync(
            method, node, $"/api/v1/servers/{Segment(instance)}{suffix}", json, ct).ConfigureAwait(false);

        if (sent.Answered)
        {
            servers.Forget();
            return Result.Success();
        }

        return Result.Failure($"{node.MemberId} would not {verb} '{instance}': {sent.Failure}");
    }

    /// <summary>A write the node accepts and settles later, followed to its end.</summary>
    private async Task<Result> AwaitedAsync(
        string instance, HttpMethod method, string suffix, string? json, string verb, CancellationToken ct)
    {
        (ClusterNode? node, string? failure) = await servers.RouteAsync(instance, ct).ConfigureAwait(false);
        if (node is null)
            return Result.Failure(failure!);

        NodeResult<CommandAccepted> accepted = await api.SendAsync(
            method, node, $"/api/v1/servers/{Segment(instance)}{suffix}", json,
            ApiContractsJson.Default.CommandAccepted, ct).ConfigureAwait(false);

        if (!accepted.Answered)
            return Result.Failure($"{node.MemberId} would not {verb} '{instance}': {accepted.Failure}");

        servers.Forget();

        return accepted.Body?.Job is { } job
            ? await WatchAsync(node, job, verb, instance, ct).ConfigureAwait(false)
            : Result.Success();
    }


    /// <summary>A body, serialized the way the node reads one.</summary>
    private static string Json<T>(T body, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> shape) =>
        JsonSerializer.Serialize(body, shape);

    /// <summary>An id inside a path, escaped so a name cannot change which route it addresses.</summary>
    private static string Segment(string value) => Uri.EscapeDataString(value);
}
