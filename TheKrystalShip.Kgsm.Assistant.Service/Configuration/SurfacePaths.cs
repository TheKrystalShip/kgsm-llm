using TheKrystalShip.KGSM.ComponentSurface;

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
/// Reading that is <see cref="ComponentSurfacePaths"/>, which every component resolves through. What
/// is this service's own is the id it is deployed under and where its state directory is.
/// </para>
/// </remarks>
internal static class SurfacePaths
{
    private const string ComponentId = "assistant";

    /// <summary>The override file's location when nobody has configured one — beside the rest of this
    /// service's state, which is the directory its unit already provisions.</summary>
    public const string DefaultOverridePath = StatePaths.DefaultDirectory + "/config-override.env";

    /// <summary>The descriptor to read about this service.</summary>
    public static string Descriptor(string? configured) =>
        ComponentSurfacePaths.Descriptor(ComponentId, configured);

    /// <summary>The command manifest to serve.</summary>
    public static string Commands(string? configured = null) =>
        ComponentSurfacePaths.Commands(ComponentId, configured);

    /// <summary>Where a change made through the panel is written.</summary>
    public static string Override(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? DefaultOverridePath : configured.Trim();
}
