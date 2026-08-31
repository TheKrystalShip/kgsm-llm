using TheKrystalShip.KGSM.Cluster;
using TheKrystalShip.KGSM.Cluster.Membership;

namespace TheKrystalShip.Kgsm.Assistant.Service.Cluster;

/// <summary>One node this assistant can reach, and the address it answers on.</summary>
/// <param name="MemberId">The node's cluster identity, which is also what its own rows are stamped with.</param>
/// <param name="Url">Where its Control Panel API answers.</param>
public sealed record ClusterNode(string MemberId, string Url);

/// <summary>
/// The nodes in this cluster: the machines that run an engine and the servers on it.
/// </summary>
/// <remarks>
/// <para>
/// Read from this member's own roster, which is the only list there is — a cluster is masterless and
/// nobody holds an authoritative table. Anchors are skipped: an anchor serves one capability and runs
/// no game servers, so asking one about servers would be asking a question it has no way to answer.
/// </para>
/// <para>
/// <b>An address that has never answered is a claim.</b> A member's row carries what it says about
/// itself until a probe confirms it, so an unverified address is offered anyway and simply fails to
/// answer — which is reported as a node that could not be reached rather than being hidden here. The
/// alternative, withholding an address until it is proven, makes a cluster that has just formed look
/// empty.
/// </para>
/// </remarks>
public sealed class NodeDirectory(MembersStore members, ClusterOptions cluster)
{
    /// <summary>Whether this member is in a cluster at all.</summary>
    public bool Clustered => cluster.Enabled;

    /// <summary>Every node this member knows of, in a stable order so two reads agree.</summary>
    public async Task<IReadOnlyList<ClusterNode>> NodesAsync(CancellationToken ct = default)
    {
        if (!cluster.Enabled)
            return [];

        IReadOnlyList<MemberRow> roster = await members.ListAsync(ct).ConfigureAwait(false);

        return
        [
            .. roster
                .Where(m => m.Enabled
                            && string.Equals(m.Kind, MemberKind.Node, StringComparison.Ordinal)
                            && m.Url.Length > 0)
                .OrderBy(m => m.MemberId, StringComparer.Ordinal)
                .Select(m => new ClusterNode(m.MemberId, m.Url.TrimEnd('/'))),
        ];
    }
}
