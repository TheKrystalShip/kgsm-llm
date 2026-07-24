namespace TheKrystalShip.Kgsm.Assistant.Fetch;

/// <summary>
/// The <c>fetch_url</c> tool's structured card payload (the surface half of its
/// <see cref="Envelope.ToolResult{TData}"/>): the final URL fetched (after any redirects), an
/// optional page title (HTML only — never fabricated for non-HTML content), the extracted text
/// (the same text the model's <c>Summary</c> is built from), and whether it was truncated (either
/// the adapter's byte cap or the summary's own char cap). Built only on a successful fetch — a
/// failed/blocked fetch stays summary-only, exactly like <see cref="Search.SearchData"/>.
/// </summary>
public sealed record FetchData(string Url, string? Title, string Text, bool Truncated);
