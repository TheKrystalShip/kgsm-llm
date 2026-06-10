namespace TheKrystalShip.Kgsm.Assistant.Service.Security;

/// <summary>
/// URL-safe base64 (RFC 4648 §5) without padding. Shared by the confirmation-token
/// service and the auth stores so the encoding lives in exactly one place.
/// </summary>
internal static class Base64Url
{
    public static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Decodes a url-safe base64 string. Throws <see cref="FormatException"/> on malformed input.</summary>
    public static byte[] Decode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
}
