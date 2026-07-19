using System.Globalization;
using System.Text.RegularExpressions;

namespace TheKrystalShip.Kgsm.Assistant.Network;

/// <summary>
/// Parses and renders the <c>open_ports</c> port specification — the single home for how the model's
/// free-text port argument (e.g. <c>"34197/udp"</c>, <c>"27015:27020/tcp, 27016/tcp"</c>) becomes a
/// validated list of <see cref="PortRule"/>s, and how that list round-trips back to a canonical string
/// (carried on the confirmation token as the config value). Shared by the dispatcher (stage-time
/// validation) and the confirm path (re-parse) so the two can never drift.
/// <para>
/// Accepted forms per entry: <c>port</c>, <c>port/proto</c>, <c>start:end</c>, <c>start:end/proto</c>.
/// Entries are separated by commas or pipes. A protocol is <c>tcp</c> or <c>udp</c>; when omitted the
/// entry expands to BOTH (one tcp rule + one udp rule), matching KGSM's UFW-style no-protocol semantics.
/// </para>
/// </summary>
public static partial class PortSpecParser
{
    private const int MinPort = 1;
    private const int MaxPort = 65535;

    [GeneratedRegex(@"^\s*(\d{1,5})(?::(\d{1,5}))?(?:/(tcp|udp))?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex EntryPattern();

    /// <summary>
    /// Parses the raw spec into a canonical, de-duplicated list of rules. Returns <c>false</c> with a
    /// user-facing <paramref name="error"/> when the spec is blank or any entry is malformed / out of
    /// range — the caller relays that to the model rather than staging a bad open.
    /// </summary>
    public static bool TryParse(string? spec, out IReadOnlyList<PortRule> rules, out string? error)
    {
        rules = Array.Empty<PortRule>();
        error = null;

        if (string.IsNullOrWhiteSpace(spec))
        {
            error = "no ports were given. Specify a port like \"34197/udp\", \"27015/tcp\", or a range " +
                    "\"27015:27020/udp\".";
            return false;
        }

        var parsed = new List<PortRule>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in spec.Split(new[] { ',', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = EntryPattern().Match(raw);
            if (!m.Success)
            {
                error = $"'{raw}' is not a valid port. Use forms like \"34197/udp\", \"27015/tcp\", or \"27015:27020/udp\".";
                return false;
            }

            var start = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var end = m.Groups[2].Success
                ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)
                : start;

            if (start < MinPort || start > MaxPort || end < MinPort || end > MaxPort)
            {
                error = $"'{raw}' is out of range — ports must be between {MinPort} and {MaxPort}.";
                return false;
            }

            if (end < start)
            {
                error = $"'{raw}' has an inverted range — the start port must not be greater than the end.";
                return false;
            }

            // No protocol → open both tcp and udp (KGSM's UFW-style no-protocol expansion).
            var protocols = m.Groups[3].Success
                ? new[] { m.Groups[3].Value.ToLowerInvariant() }
                : new[] { "tcp", "udp" };

            foreach (var proto in protocols)
            {
                var key = $"{start}:{end}/{proto}";
                if (seen.Add(key))
                    parsed.Add(new PortRule(start, end, proto));
            }
        }

        if (parsed.Count == 0)
        {
            error = "no ports were given.";
            return false;
        }

        rules = parsed;
        return true;
    }

    /// <summary>
    /// Renders a rule list to the canonical spec string — <c>start:end/proto</c> for a range,
    /// <c>port/proto</c> for a single port, comma-joined. The inverse of <see cref="TryParse"/>; used to
    /// carry the validated ports on the confirmation token so the confirm path re-parses exactly what
    /// was staged.
    /// </summary>
    public static string ToCanonical(IReadOnlyList<PortRule> rules) =>
        string.Join(",", rules.Select(r =>
            r.Start == r.End ? $"{r.Start}/{r.Protocol}" : $"{r.Start}:{r.End}/{r.Protocol}"));

    /// <summary>A human list for grounding text — each rule as <c>port/proto</c> or <c>start-end/proto</c>.</summary>
    public static string ToDisplay(IReadOnlyList<PortRule> rules) =>
        string.Join(", ", rules.Select(r => r.ToDisplay()));
}
