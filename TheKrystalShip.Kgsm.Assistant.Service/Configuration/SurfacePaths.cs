namespace TheKrystalShip.Kgsm.Assistant.Service.Configuration;

/// <summary>
/// Where this service's own surface reads and writes, resolved for the standing it is deployed in.
/// </summary>
/// <remarks>
/// <para>
/// This component is a leaf or an anchor depending on the deployment: on a machine standing alone it
/// serves the host it runs on and that node's panel administers it as one of its services; in a
/// cluster it answers for every node and is reached at an address. Its build writes both descriptors
/// and the deploy installs the one its standing calls for, clearing the other — so exactly one of the
/// two locations holds a file, and which one is the answer to what this component currently is.
/// </para>
/// <para>
/// The anchor location is checked first. If both somehow hold a file, the deploy did not finish, and
/// a peer of every node is the more consequential of the two readings to get right.
/// </para>
/// </remarks>
internal static class SurfacePaths
{
    private const string ComponentId = "assistant";

    /// <summary>Where a deploy installs the descriptor of a component that is an anchor here.</summary>
    public const string AnchorDescriptor = "/var/lib/kgsm/anchors/" + ComponentId + ".json";

    /// <summary>Where a deploy installs the descriptor of a component that is a leaf here.</summary>
    public const string LeafDescriptor = "/var/lib/kgsm/leaves/" + ComponentId + ".json";

    /// <summary>The command manifest of a component that is an anchor here.</summary>
    public const string AnchorCommands = "/var/lib/kgsm/anchors/commands/" + ComponentId + ".json";

    /// <summary>The command manifest of a component that is a leaf here.</summary>
    public const string LeafCommands = "/var/lib/kgsm/leaves/commands/" + ComponentId + ".json";

    /// <summary>The override file's location when nobody has configured one — beside the rest of this
    /// service's state, which is the directory its unit already provisions.</summary>
    public const string DefaultOverridePath = StatePaths.DefaultDirectory + "/config-override.env";

    /// <summary>
    /// The descriptor to read. A configured path is an operator naming one deliberately and wins,
    /// whether or not it exists — being told where to look and finding nothing is a fact worth
    /// reporting, and quietly reading somewhere else would hide it.
    /// </summary>
    public static string Descriptor(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();

        return File.Exists(AnchorDescriptor) ? AnchorDescriptor : LeafDescriptor;
    }

    /// <summary>Where a change made through the panel is written.</summary>
    public static string Override(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? DefaultOverridePath : configured.Trim();

    /// <summary>
    /// The command manifest to serve, read the same way and for the same reason as the descriptor:
    /// the deploy installs it beside whichever descriptor it installed, so its location is the same
    /// answer to what this component currently is.
    /// </summary>
    public static string Commands() =>
        File.Exists(AnchorCommands) ? AnchorCommands : LeafCommands;
}
