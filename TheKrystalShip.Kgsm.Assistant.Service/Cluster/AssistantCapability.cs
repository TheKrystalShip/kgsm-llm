using TheKrystalShip.KGSM.Cluster;
using TheKrystalShip.KGSM.Cluster.Membership;

namespace TheKrystalShip.Kgsm.Assistant.Service.Cluster;

/// <summary>
/// The facts this assistant states about itself for the cluster to read.
/// </summary>
/// <remarks>
/// Read through the holder of the capability, never off whichever member states them: a member that
/// answered for an assistant it does not hold would send every person's chat somewhere nobody
/// assigned.
/// </remarks>
public static class AssistantFacts
{
    /// <summary>The address a person's browser reaches the assistant at.</summary>
    public const string Url = "assistant.url";
}

/// <summary>
/// Where this assistant stands in its cluster: the member that answers for the whole of it, or a
/// candidate standing by while another does.
/// </summary>
/// <remarks>
/// <para>
/// One assistant per cluster, for the same reason there is one anchor holding the accounts: a person
/// asks a question and there is one answer to where it goes. A second install is a candidate — it
/// claims only into an assignment nobody holds, re-reads, and stands down when the cluster names
/// somebody else.
/// </para>
/// <para>
/// <b>Standing by is not being switched off.</b> A machine that is not in a cluster serves everything
/// it always has, which is what a standalone install is; and the address is published whatever the
/// standing, because a reader resolves the holder first and takes the fact off that member alone — so
/// stating it early is what lets a reassignment need no restart anywhere.
/// </para>
/// </remarks>
public sealed class AssistantCapabilityWorker(
    ClusterOptions cluster,
    ClusterStateStore state,
    SelfPublications publications,
    AssistantClusterSettings settings,
    ILogger<AssistantCapabilityWorker> logger)
    : ClusterCapabilityWorker(ClusterCapability.Assistant, cluster, state, logger)
{
    protected override void OnStarting()
    {
        // Where a browser goes, which is a different question from where members reach this service
        // even when one address answers both. Stated rather than inferred from the roster: an
        // assistant on a LAN address for member traffic behind a public vhost for browsers has two
        // answers, and handing a browser the first sends it somewhere it cannot reach. Blank states
        // nothing, and a reader falls back to the address members learned.
        if (settings.PublicBaseUrl is { Length: > 0 } browserUrl)
            publications.Publish(AssistantFacts.Url, browserUrl);
    }

    /// <remarks>
    /// There is nothing outside this member's own state to reconcile: the address is a published fact,
    /// which rides the member's own entry and is scoped by its reader. The standing itself is what
    /// callers read, and the base class already holds it.
    /// </remarks>
    protected override Task OnStandingAsync(CapabilityHolding holding, bool changed, CancellationToken ct) =>
        Task.CompletedTask;
}

/// <summary>The resolved cluster-facing values, so nothing downstream re-reads a raw setting.</summary>
/// <param name="MemberId">This member's identity, derived from the machine name when unset.</param>
/// <param name="PublicBaseUrl">The address a browser uses, or empty when there is nothing to state.</param>
/// <param name="FleetWindow">How long an answer from the nodes is reused before they are asked again.</param>
public sealed record AssistantClusterSettings(
    string MemberId, string PublicBaseUrl, TimeSpan FleetWindow);
