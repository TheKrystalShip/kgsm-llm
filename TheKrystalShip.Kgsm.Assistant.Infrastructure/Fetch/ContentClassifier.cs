namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Fetch;

/// <summary>
/// Decides whether a response's content-type is text <c>fetch_url</c> may read. Deliberately
/// conservative: a missing content-type is refused rather than sniffed/guessed (the honesty rule —
/// don't fabricate a "this is text" judgment). <c>text/html</c> is textual but handled specially by
/// the caller (run through <see cref="HtmlTextExtractor"/>); every other textual type (including
/// <c>text/plain</c>, which is how a raw Dockerfile or <c>server.properties</c> off GitHub typically
/// arrives) is returned as-is.
/// </summary>
internal static class ContentClassifier
{
    private static readonly string[] TextualExact =
    [
        "application/json",
        "application/xml",
        "application/xhtml+xml",
        "application/yaml",
        "application/x-yaml",
        "application/toml",
    ];

    public static bool IsTextual(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return false;

        if (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            return true;

        return Array.Exists(TextualExact, t => string.Equals(t, contentType, StringComparison.OrdinalIgnoreCase));
    }
}
