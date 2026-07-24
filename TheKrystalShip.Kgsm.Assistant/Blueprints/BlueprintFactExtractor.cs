using System.Text.RegularExpressions;

using TheKrystalShip.Kgsm.Assistant.Ports;

namespace TheKrystalShip.Kgsm.Assistant.Blueprints;

/// <summary>
/// The code-driven, best-effort extraction behind <see cref="BlueprintResearchAggregator"/>: regex
/// heuristics over fetched page text, never a nested model call (keeps the pipeline viable at 12B —
/// see <c>IBlueprintResearch</c>'s doc comment). A field this cannot confidently source is simply
/// omitted from <see cref="BlueprintResearchFindings.Fields"/> — never a guessed value; the draft step
/// downstream renders every omitted field as YAML <c>null</c>, kgsm's "unknown," never a fabricated 0.
/// </summary>
internal static class BlueprintFactExtractor
{
    // A Steam App ID as it commonly appears in setup docs: "App ID: 1234567", "app/1234567" (a
    // steamdb/store URL), or a steamcmd invocation ("+app_update 1234567").
    private static readonly Regex SteamAppId = new(
        @"(?:app(?:\/|\s*id[:\s]+|_update\s+))(\d{3,8})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A relative launch command for the server binary/script: "./ServerName.sh" / "./server.x86_64".
    private static readonly Regex ExecutableHint = new(
        @"\.\/([A-Za-z0-9_.\-]+\.(?:sh|x86_64|x64))\b", RegexOptions.Compiled);

    // "port 27015" / "ports: 27015" style mentions — a single representative port, not a full range.
    private static readonly Regex PortHint = new(
        @"\bport(?:s)?\b[^.\n]{0,30}?(\d{4,5})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] NotSelfHostablePhrases =
    [
        "cannot be self-hosted", "can't be self-hosted", "no dedicated server",
        "not possible to host your own", "official servers only", "no way to host your own",
    ];

    private static readonly string[] WindowsOnlyPhrases =
    [
        "windows only", "windows-only server", "no linux server", "does not support linux",
        "linux is not supported",
    ];

    private static readonly string[] NativeLinuxSignals =
    [
        "linux dedicated server", "linux server", "steamcmd", "dedicated server for linux",
        "server files", "linux",
    ];

    /// <summary>Runs the feasibility gate then, only when feasible, the field extraction — over the
    /// combined text of every successfully fetched page. <paramref name="pages"/> must be non-empty
    /// (the caller returns <see cref="BlueprintFeasibility.Inconclusive"/> itself when nothing fetched).</summary>
    public static BlueprintResearchFindings Extract(string game, IReadOnlyList<(string Url, string Text)> pages)
    {
        var urls = pages.Select(p => p.Url).Distinct().ToArray();
        var combinedLower = string.Join("\n\n", pages.Select(p => p.Text)).ToLowerInvariant();

        if (Array.Exists(NotSelfHostablePhrases, combinedLower.Contains))
            return new BlueprintResearchFindings(
                BlueprintFeasibility.NotSelfHostable, null, [], urls,
                $"Sources for \"{game}\" indicate it cannot be self-hosted.");

        bool mentionsLinux = Array.Exists(NativeLinuxSignals, combinedLower.Contains);
        bool windowsOnly = Array.Exists(WindowsOnlyPhrases, combinedLower.Contains);
        if (!mentionsLinux || windowsOnly)
            return new BlueprintResearchFindings(
                BlueprintFeasibility.NoNativeLinuxServer, null, [], urls,
                $"Sources for \"{game}\" did not confirm a native-Linux dedicated server.");

        var fields = new List<BlueprintResearchField>();
        foreach (var (url, text) in pages)
        {
            TryAdd(fields, "steam_app_id", SteamAppId, text, url);
            TryAdd(fields, "executable_file", ExecutableHint, text, url);
            TryAdd(fields, "ports", PortHint, text, url);
        }

        return new BlueprintResearchFindings(
            BlueprintFeasibility.Feasible, game, fields, urls,
            $"Sources for \"{game}\" describe a native-Linux dedicated server; sourced {fields.Count} field(s) from {urls.Length} page(s).");
    }

    /// <summary>Adds the first match for <paramref name="field"/> found across pages — later pages never
    /// override an already-sourced field, so the citation is always the FIRST page that supported it.</summary>
    private static void TryAdd(List<BlueprintResearchField> fields, string field, Regex pattern, string text, string url)
    {
        if (fields.Exists(f => f.Name == field))
            return;

        var match = pattern.Match(text);
        if (match.Success)
            fields.Add(new BlueprintResearchField(field, match.Groups[1].Value, url));
    }
}
