namespace TheKrystalShip.Kgsm.Assistant.Ports;

/// <summary>
/// Load averages, as the host reports them. Kept as the strings the host printed rather than parsed
/// numbers — a parse that fails would have to invent a value, and an unparsed string is honest.
/// </summary>
public sealed record HostLoad(string OneMin, string FiveMin, string FifteenMin);

/// <summary>Host memory totals, as reported.</summary>
public sealed record HostMemory(string Total, string Used, string Free, string Available);

/// <summary>
/// The host's own vitals. Any member may be null when the host reported nothing for it — a null is
/// "not reported", never a zero.
/// </summary>
public sealed record HostFacts(
    FactsState State,
    string? Uptime,
    HostLoad? Load,
    HostMemory? Memory,
    HostDisk? Disk,
    string? ExternalIp,
    bool? RebootRequired);

/// <summary>
/// What is bound on the host's ports and where two instances want the same one. A conflict is the
/// engine's own finding, not a comparison this layer derives.
/// </summary>
public sealed record HostPortUsage(
    FactsState State,
    IReadOnlyList<string> UsedPorts,
    IReadOnlyList<string> Conflicts);

/// <summary>
/// Facts about the host machine itself rather than any one instance — what backs the model-facing
/// <c>host_info</c> tool. Read-only: the host's own lifecycle (shutdown, reboot) is deliberately not
/// on this seam and no tool reaches it, because rebooting the machine is a blast radius nothing in a
/// chat turn should be able to propose.
/// <para>
/// An unreachable source is <see cref="FactsState.Unavailable"/>, never an empty reading, and
/// implementations MUST NOT throw.
/// </para>
/// </summary>
public interface IHostFacts
{
    /// <summary>Reads the host's uptime, load, memory, disk, external address and reboot flag.</summary>
    Task<HostFacts> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads the host's bound ports and any port conflicts between instances.</summary>
    Task<HostPortUsage> GetPortUsageAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IHostFacts"/> for a host that has wired no engine: both reads fail closed as
/// <see cref="FactsState.Unavailable"/>, so embedding the assistant library never breaks DI.
/// <c>AddKgsmAssistant</c> registers this with <c>TryAddSingleton</c> and <c>AddKgsmAdapters</c>
/// registers the real adapter afterward, which is the one resolved.
/// </summary>
public sealed class UnavailableHostFacts : IHostFacts
{
    public Task<HostFacts> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new HostFacts(FactsState.Unavailable, null, null, null, null, null, null));

    public Task<HostPortUsage> GetPortUsageAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new HostPortUsage(FactsState.Unavailable, [], []));
}
