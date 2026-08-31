namespace TheKrystalShip.Kgsm.Assistant.Service.Cluster;

/// <summary>
/// The answer to a call whose outcome is only whether it worked.
/// </summary>
/// <remarks>
/// A node that answers with a body still returns this, and the body is discarded — what came back is
/// not what the caller is asking about. It exists so the send path has one shape rather than a
/// body-returning overload and a void one that drift apart.
/// </remarks>
public sealed record NodeNothing;
