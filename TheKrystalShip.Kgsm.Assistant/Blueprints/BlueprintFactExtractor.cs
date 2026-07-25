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
    // The DEDICATED-SERVER Steam App ID, taken ONLY from an unambiguous server-download context: a
    // steamcmd "+app_update <id>" command (the id steamcmd pulls the server files with), or text that
    // explicitly ties the id to the dedicated/server app. A bare store-page "app/<id>" URL or a plain
    // "App ID: <id>" is deliberately NOT matched — that is typically the CLIENT app id (e.g. Terraria's
    // 105600, Necesse's client 1169040), and a client id is the wrong thing to SteamCMD-install; leaving
    // it unsourced (→ the schema's 0 = "not a Steam download") is the honest, safe default.
    private static readonly Regex SteamAppId = new(
        @"(?:app_update\s+|(?:dedicated[- ]?server|server)[^\d\n]{0,40}?app\s*id[:\s]+)(\d{3,8})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // The server binary/script, when INVOKED: "./ServerName.sh", "bash StartServer.sh", "sh run.sh".
    // Highest confidence — an explicit invocation is unambiguously the thing you launch.
    private static readonly Regex ExecutableHint = new(
        @"(?:\.\/|\b(?:bash|sh)\s+)([A-Za-z0-9_.\-]+\.(?:sh|x86_64|x64))\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Fallback: a launcher-LOOKING filename mentioned without an explicit invoker (e.g. Necesse's
    // "StartServer-nogui.sh", referenced by name rather than as "./…"). Gated to names containing
    // start/server/launch/run so it doesn't grab an unrelated script from the same doc (steamcmd.sh,
    // install.sh). A wrong pick is still caught downstream by the boot+listen verification.
    private static readonly Regex ExecutableNameHint = new(
        @"\b([A-Za-z0-9_.\-]*(?:start|server|launch|run)[A-Za-z0-9_.\-]*\.(?:sh|x86_64|x64))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "port 27015" / "ports: 27015" style mentions — a single representative port, not a full range.
    private static readonly Regex PortHint = new(
        @"\bport(?:s)?\b[^.\n]{0,30}?(\d{4,5})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A documented headless launch command: a server binary/script (…​.sh / .x86_64 / .x64) invoked with
    // its argument tail — the flags that make it boot non-interactively (e.g. Terraria's
    // "-autocreate 3 -world … -worldname …", which it needs to skip the interactive "Choose World" prompt
    // that otherwise leaves it hung and never listening). Captures the run of args up to a line/markup
    // break; a "chmod +x ./server.x86_64" line has no trailing flags after the binary, so it isn't caught.
    private static readonly Regex LaunchArgsHint = new(
        @"(?:\.\/)?[A-Za-z0-9_.\-]+\.(?:sh|x86_64|x64)\s+((?:[-+][^\r\n<>`|]{0,200}))",
        RegexOptions.Compiled);

    // Distinctive server-READY log lines (not startup banners). Used only as a SECONDARY readiness signal —
    // the verify bar's primary sensor is port-reachability — and only when one appears VERBATIM in a fetched
    // page, so the regex is sourced, never guessed. Curated to true "now accepting connections" lines to
    // avoid a premature-success match on an early boot banner. First present wins.
    private static readonly string[] StartupReadyPhrases =
    [
        "Server started", "Hosting game at", "Started Server", "Server listening",
        "Ready for connections", "listening on port", "For help, type",
    ];

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
            TryAdd(fields, "executable_file", ExecutableNameHint, text, url); // fallback: launcher-looking name, no explicit invoker
            TryAdd(fields, "ports", PortHint, text, url);
            TryAdd(fields, "executable_arguments", LaunchArgsHint, text, url, clean: CleanArgs);
            TryAddPhrase(fields, "startup_success_regex", StartupReadyPhrases, text, url);
        }

        return new BlueprintResearchFindings(
            BlueprintFeasibility.Feasible, game, fields, urls,
            $"Sources for \"{game}\" describe a native-Linux dedicated server; sourced {fields.Count} field(s) from {urls.Length} page(s).");
    }

    /// <summary>Adds the first match for <paramref name="field"/> found across pages — later pages never
    /// override an already-sourced field, so the citation is always the FIRST page that supported it. An
    /// optional <paramref name="clean"/> normalizes the raw capture (e.g. trims trailing prose punctuation
    /// off an extracted argument tail); a value that cleans down to empty is not added.</summary>
    private static void TryAdd(
        List<BlueprintResearchField> fields, string field, Regex pattern, string text, string url,
        Func<string, string>? clean = null)
    {
        if (fields.Exists(f => f.Name == field))
            return;

        var match = pattern.Match(text);
        if (!match.Success)
            return;

        var value = match.Groups[1].Value;
        if (clean is not null)
            value = clean(value);
        if (!string.IsNullOrWhiteSpace(value))
            fields.Add(new BlueprintResearchField(field, value, url));
    }

    /// <summary>Trims a captured launch-argument tail down to just the arguments. HTML→text flattening
    /// collapses a doc's line breaks, so the greedy capture can run past the command into the following
    /// sentence ("… -config serverconfig.txt . The most important keys …"); cut at the first sentence
    /// boundary (a period followed by whitespace — real flag values like <c>world.wld</c> have no
    /// trailing space) and strip dangling prose punctuation. An over-capture that still slips through is
    /// caught downstream by the empirical boot+listen verification, never shipped blind.</summary>
    private static string CleanArgs(string raw)
    {
        var a = raw.Trim();
        var sentenceEnd = a.IndexOf(". ", StringComparison.Ordinal);
        if (sentenceEnd >= 0)
            a = a[..sentenceEnd];
        return a.Trim().TrimEnd('.', ',', ';', ')');
    }

    /// <summary>Adds <paramref name="field"/> as the first <paramref name="phrases"/> entry that appears
    /// VERBATIM (case-sensitive, as it would in real server output) in the page text — a sourced literal,
    /// never a guess. Same first-page-wins semantics as <see cref="TryAdd"/>.</summary>
    private static void TryAddPhrase(
        List<BlueprintResearchField> fields, string field, string[] phrases, string text, string url)
    {
        if (fields.Exists(f => f.Name == field))
            return;

        var hit = Array.Find(phrases, text.Contains);
        if (hit is not null)
            fields.Add(new BlueprintResearchField(field, hit, url));
    }
}
