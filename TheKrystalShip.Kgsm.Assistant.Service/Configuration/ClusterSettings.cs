using TheKrystalShip.KGSM.ComponentConfig;

namespace TheKrystalShip.Kgsm.Assistant.Service.Configuration;

/// <summary>
/// What this service states about itself as a member of a cluster. Bound from the "Cluster" section.
/// </summary>
/// <remarks>
/// <para>
/// Whether it is a member at all is not here: that is the shared secret in
/// <c>/etc/kgsm/kgsm-cluster.env</c>, read through the cluster package so every member on the machine
/// spells the key identically. A machine without it runs this service exactly as a standalone install
/// always has, and these values are then never read.
/// </para>
/// <para>
/// The secret is deliberately absent from this class and from the settings file. It is one host-level
/// value shared by every member on the machine, and a second declaration of it would be a second
/// place to set it — where one blank and one filled look identical from the outside.
/// </para>
/// </remarks>
[ConfigSection(Section)]
public sealed class AssistantClusterOptions
{
    public const string Section = "Cluster";

    /// <summary>
    /// This member's identity in the cluster. Blank derives it from the machine name.
    /// </summary>
    /// <remarks>
    /// Per member, never per host: a machine running both a node and this service holds two members
    /// with two names, and giving them one would make disabling either disable both.
    /// </remarks>
    /// <panel>The name this assistant is known by inside the cluster. Leave blank to name it after the
    /// machine it runs on. Changing it makes this a different member, which every other member has to
    /// be told about.</panel>
    [ConfigField("clusterMemberId", "Member id", Group = "cluster")]
    public string MemberId { get; set; } = "";

    /// <summary>
    /// The address a browser reaches this assistant at, stated rather than observed.
    /// </summary>
    /// <remarks>
    /// A member learns addresses by being reached at them, and the configured one wins. Blank is the
    /// ordinary case for a machine that can see its own address; it is not blank behind a reverse
    /// proxy, where what a browser uses and what a member connects to are different names.
    /// </remarks>
    /// <panel>The public address people reach this assistant's chat at. Leave blank unless it sits
    /// behind a reverse proxy, where the address a browser uses is not the one other machines connect
    /// to.</panel>
    [ConfigField("clusterPublicBaseUrl", "Public address", Group = "cluster", Type = ConfigType.String)]
    public string PublicBaseUrl { get; set; } = "";

    /// <summary>
    /// Where this machine is reached from the internet, for the cluster's DNS anchor to point the
    /// capability's name at.
    /// </summary>
    /// <remarks>
    /// A host rather than an address with a scheme: the DNS anchor publishes the capability's name as an
    /// alias of it, so it is normally the dynamic-DNS name the network keeps pointed at a changing home
    /// address. Blank states none, and the name is not published.
    /// </remarks>
    /// <panel>Where this machine is reached from the internet — normally the dynamic-DNS name its network
    /// keeps pointed at a changing home address, such as example.ddns.net. In a cluster with a DNS anchor,
    /// the assistant capability's name points at it while this assistant holds it, and this assistant
    /// serves that name on a certificate the DNS anchor issues. Only read when this assistant is part of a
    /// cluster.</panel>
    [ConfigField("clusterPublicHost", "Public host", Group = "cluster", Type = ConfigType.String)]
    public string PublicHost { get; set; } = "";

    /// <summary>
    /// How long an answer from the cluster's nodes is reused before they are asked again, in seconds.
    /// </summary>
    /// <remarks>
    /// Seconds rather than the minutes the engine on this machine is cached for, because the two are
    /// bounded by different things. A local read is told when it goes stale — the engine says a server
    /// was installed — so it can be held until then. Nothing tells this member that another machine
    /// changed, so the window is the whole of what bounds it. Zero asks every node on every read,
    /// which is correct and turns one turn into a burst of them.
    /// </remarks>
    /// <panel>How long the assistant reuses what the cluster's other machines told it before asking
    /// again. Only read when this assistant is part of a cluster.</panel>
    [ConfigField("clusterFleetWindowSec", "Fleet answer window", Group = "cluster", Min = 0, Unit = "s")]
    public int FleetWindowSeconds { get; set; } = 15;

    /// <summary>This member's id, derived from the machine name when nothing is configured.</summary>
    public string ResolveMemberId() =>
        string.IsNullOrWhiteSpace(MemberId)
            ? Environment.MachineName.Trim().ToLowerInvariant() + "-assistant"
            : MemberId.Trim();
}
