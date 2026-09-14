namespace TheKrystalShip.Kgsm.Assistant.Service.Speech;

/// <summary>
/// Where kgsm-speech listens on this host.
/// </summary>
/// <remarks>
/// <b>Named here because it is KGSM's path.</b> The speech client is shared with anything else that
/// listens or speaks and cannot know which daemon is on the other end, so whoever connects is the one
/// that knows where to connect. Stated once rather than at each adapter: two surfaces of the same
/// leaf guessing the path differently is a bug that only shows up on the host where somebody moved
/// the socket.
/// </remarks>
internal static class HostSpeechSocket
{
    public const string Default = "/run/kgsm-speech/speech.sock";

    /// <summary>The configured path, or the standard one when nothing is configured.</summary>
    public static string OrDefault(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? Default : configured;
}
