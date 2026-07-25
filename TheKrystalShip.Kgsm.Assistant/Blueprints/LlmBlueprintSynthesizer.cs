using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;

using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Blueprints;

/// <summary>
/// Model-synthesis field extraction (<see cref="IBlueprintSynthesizer"/>): one LLM call reads the fetched
/// pages and returns sourced native-server fields — the capable path a regex can't match, because judging
/// "which of these pages describes the OFFICIAL native launch, and what are its args" needs reading in
/// context, not pattern-matching.
/// <para>
/// No-fabrication is layered, because a model left to itself will guess: the prompt orders null-not-guess
/// and a per-field source citation; a field is kept only when it cites one of the pages actually fetched;
/// the copy-from-source fields (<c>executable_file</c>, <c>steam_app_id</c>) must additionally appear
/// verbatim in the fetched text; and the whole pipeline's empirical boot+listen verification is the final
/// backstop — a synthesized field that doesn't actually run never reaches the catalog. Any failure
/// (unconfigured model, transport error, unparseable reply, no required field) returns
/// <see langword="null"/>, and research falls back to the deterministic regex extractor.
/// </para>
/// </summary>
public sealed class LlmBlueprintSynthesizer : IBlueprintSynthesizer
{
    // Per-page text budget fed to the model: enough to carry a setup guide's command block, bounded so a
    // few large pages can't blow the context window (Ollama NumCtx is a fixed VRAM reservation).
    private const int MaxCharsPerPage = 8000;

    private const string SystemPrompt =
        "You extract facts for setting up a NATIVE Linux dedicated game server from the web pages the user " +
        "provides, for a tool that installs servers directly on Linux (NOT with Docker). Output ONLY a single " +
        "JSON object and nothing else.\n\n" +
        "Rules:\n" +
        "- Use ONLY facts explicitly stated in the provided pages. If a fact is not stated, use null. NEVER " +
        "guess, infer, or invent a value — a null is far better than a wrong value.\n" +
        "- We install the game's OFFICIAL native Linux server: a script or binary run directly on the host. " +
        "Do NOT report a Docker/container entrypoint (e.g. entry.sh, docker-entrypoint.sh, or a start script " +
        "defined inside a Dockerfile) as the executable — look past containerization wrappers to the native " +
        "launch script/binary the game itself ships.\n" +
        "- executable_file is the thing you RUN. Some servers run THROUGH an interpreter — report the " +
        "interpreter as executable_file and put the artifact in executable_arguments:\n" +
        "    * a Java server (a .jar): executable_file is \"java\", and \"-jar <thefile>.jar\" goes in " +
        "executable_arguments — NOT the .jar as executable_file.\n" +
        "    * a .NET server (a .dll): executable_file is \"dotnet\", and \"<thefile>.dll\" goes in " +
        "executable_arguments — NOT the .dll as executable_file.\n" +
        "  Otherwise executable_file is the native script/binary run directly (e.g. StartServer.sh, " +
        "srv.x86_64, DedicatedServer).\n" +
        "- PREFER THE GAME'S OWN LAUNCH SCRIPT over the raw binary. When the game ships a wrapper script " +
        "(e.g. start_server.sh, _launch.sh, start_server_bepinex.sh) AND a raw binary (e.g. " +
        "valheim_server.x86_64, CoreKeeperServer.x86_64), the SCRIPT is executable_file — it sets up the " +
        "runtime the binary needs (exports LD_LIBRARY_PATH, cd's into the install dir, sets env). Running " +
        "the raw binary directly usually fails to load its bundled libraries. Use the raw binary ONLY when " +
        "the docs show it run directly with no wrapper script present. (This is still NOT a Docker " +
        "entrypoint — a Docker entry.sh stays excluded.)\n" +
        "- executable_subdirectory: if the executable lives in a subfolder of the install dir (e.g. the docs " +
        "run \"bin/x64/factorio\"), put the SUBFOLDER (\"bin/x64\") here and ONLY the filename (\"factorio\") " +
        "in executable_file. If the executable sits at the install root, use null.\n" +
        "- executable_arguments must be the flags that launch the server HEADLESS / non-interactively (so it " +
        "boots and listens without waiting for keyboard input). Keep them MINIMAL: only the flags the docs " +
        "show are needed to launch and listen. Omit cosmetic/optional flags.\n" +
        "- executable_arguments runs under KGSM, which substitutes these placeholders at server-creation " +
        "time — USE them instead of a literal path/name or a doc placeholder like [World], <name>, or " +
        "YOUR_SAVE:\n" +
        "    $instance_level_name  = the world / save / level name (use wherever the docs put a world or " +
        "save NAME)\n" +
        "    $instance_saves_dir   = absolute path to the saves/data directory\n" +
        "    $instance_install_dir = absolute path to the server's install directory\n" +
        "  These three are the ONLY variables that exist. NEVER write any other $VARIABLE — not " +
        "$SERVER_PORT, $SERVER_NAME, $SERVER_PASSWORD, $PORT, or anything else: KGSM does not define them, " +
        "so they resolve to EMPTY and the server fails to boot. For a value the docs leave for the user " +
        "(a port, a server name, a password), write a concrete literal from the docs (e.g. \"-port 2456\") " +
        "or OMIT that optional flag entirely. Do NOT include an absolute host path that won't exist on a " +
        "fresh install (e.g. /opt/<game>/server-settings.json) — use a $instance_* path or omit the flag. " +
        "If the executable is a wrapper SCRIPT that reads its own config, prefer EMPTY arguments unless the " +
        "docs clearly show flags it accepts. Examples: \"-world <SaveName>\" becomes " +
        "\"-world $instance_level_name\"; \"--start-server /path/to/save\" becomes " +
        "\"--start-server $instance_saves_dir/$instance_level_name\". Never invent flags.\n" +
        "- steam_app_id is the DEDICATED SERVER app id (the id used in a `steamcmd +app_update <id>` command), " +
        "NOT the client/game app id from a store page. client_steam_app_id is the CLIENT/game app id players " +
        "buy and launch to connect (the store-page id), when the page states it; else null. If the game is " +
        "not installed through SteamCMD at all, use null for steam_app_id.\n" +
        "- requires_steam_account is a WARNING (it decides whether to even attempt an install), not a " +
        "blueprint value — so here you MAY reasonably infer it, unlike the fields above. Set it true ONLY " +
        "when the SERVER DOWNLOAD itself needs an owning account. Strong signals: the steamcmd command logs " +
        "in with a personal account (`+login <username>`) rather than `+login anonymous`; the docs " +
        "explicitly say the SERVER files can't be downloaded anonymously; or the server downloads under the " +
        "SAME app id as the paid client (no separate server app). IMPORTANT: if the DEDICATED-SERVER app id " +
        "is DIFFERENT from the client app id, a free standalone dedicated-server app exists → ownership is " +
        "NOT required → set false. Generic 'you must own the game to play' prose about the CLIENT is NOT " +
        "sufficient on its own. When in doubt, set false — the automated test-install uses anonymous login " +
        "and a false 'true' wrongly declines a game that would actually install.\n" +
        "- For every non-null field, set its matching *_source to the EXACT page URL (copied from the list the " +
        "user gives you) that stated it. If you cannot cite a provided URL for a value, use null for the value.\n\n" +
        "JSON shape (use exactly these keys):\n" +
        "{\n" +
        "  \"self_hostable\": true|false|null,\n" +
        "  \"native_linux_server\": true|false|null,\n" +
        "  \"requires_steam_account\": true|false|null,\n" +
        "  \"executable_file\": string|null, \"executable_file_source\": string|null,\n" +
        "  \"executable_subdirectory\": string|null, \"executable_subdirectory_source\": string|null,\n" +
        "  \"executable_arguments\": string|null, \"executable_arguments_source\": string|null,\n" +
        "  \"ports\": string|null, \"ports_source\": string|null,\n" +
        "  \"steam_app_id\": string|null, \"steam_app_id_source\": string|null,\n" +
        "  \"client_steam_app_id\": string|null, \"client_steam_app_id_source\": string|null,\n" +
        "  \"startup_success_regex\": string|null, \"startup_success_regex_source\": string|null\n" +
        "}";

    private readonly ILlmClient _llm;
    private readonly ILogger<LlmBlueprintSynthesizer> _logger;

    public LlmBlueprintSynthesizer(ILlmClient llm, ILogger<LlmBlueprintSynthesizer> logger)
    {
        _llm = llm;
        _logger = logger;
    }

    public async Task<BlueprintResearchFindings?> SynthesizeAsync(
        string game, IReadOnlyList<(string Url, string Text)> pages, CancellationToken cancellationToken = default)
    {
        try
        {
            var urls = pages.Select(p => p.Url).Distinct().ToArray();
            var combined = string.Join("\n", pages.Select(p => p.Text));

            var messages = new[]
            {
                LlmMessage.System(SystemPrompt),
                LlmMessage.User(BuildUserPrompt(game, pages)),
            };

            var response = await _llm.ChatAsync(messages, tools: null, think: false, cancellationToken);
            if (response.IsFailure || string.IsNullOrWhiteSpace(response.Value?.Content))
                return null;

            var json = ExtractJsonObject(response.Value!.Content!);
            if (json is null)
                return null;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (ReadBool(root, "self_hostable") == false)
                return new BlueprintResearchFindings(
                    BlueprintFeasibility.NotSelfHostable, null, [], urls,
                    $"Sources for \"{game}\" indicate it cannot be self-hosted.");
            if (ReadBool(root, "native_linux_server") == false)
                return new BlueprintResearchFindings(
                    BlueprintFeasibility.NoNativeLinuxServer, null, [], urls,
                    $"Sources for \"{game}\" did not confirm a native-Linux dedicated server.");
            if (ReadBool(root, "requires_steam_account") == true)
                return new BlueprintResearchFindings(
                    BlueprintFeasibility.RequiresSteamAccount, null, [], urls,
                    $"Sources for \"{game}\" indicate its server files require a Steam account that owns the game — the autonomous anonymous test-install can't download it.");

            var fields = new List<BlueprintResearchField>();
            AddField(fields, root, "executable_file", urls, combined, requireInText: true);
            AddField(fields, root, "executable_subdirectory", urls, combined, requireInText: true);
            AddField(fields, root, "executable_arguments", urls, combined, requireInText: false, normalize: RejectForeignPlaceholders);
            AddField(fields, root, "ports", urls, combined, requireInText: false, normalize: NormalizePort);
            AddField(fields, root, "steam_app_id", urls, combined, requireInText: true, normalize: NormalizeDigits);
            AddField(fields, root, "client_steam_app_id", urls, combined, requireInText: true, normalize: NormalizeDigits);
            AddField(fields, root, "startup_success_regex", urls, combined, requireInText: false);

            // The one strictly-required native field: if synthesis couldn't source it, hand off to the
            // deterministic extractor rather than draft a blueprint that can't launch.
            if (!fields.Exists(f => f.Name == "executable_file"))
                return null;

            return new BlueprintResearchFindings(
                BlueprintFeasibility.Feasible, game, fields, urls,
                $"Synthesized {fields.Count} sourced field(s) for \"{game}\" from {urls.Length} page(s).");
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Blueprint field synthesis failed for \"{Game}\"; falling back to deterministic extraction", game);
            return null;
        }
    }

    private static string BuildUserPrompt(string game, IReadOnlyList<(string Url, string Text)> pages)
    {
        var sb = new StringBuilder();
        sb.Append("Game: ").AppendLine(game);
        sb.AppendLine().AppendLine("Pages:");
        var i = 1;
        foreach (var (url, text) in pages)
        {
            sb.Append('[').Append(i++).Append("] ").AppendLine(url);
            sb.AppendLine(text.Length > MaxCharsPerPage ? text[..MaxCharsPerPage] : text).AppendLine();
        }
        sb.AppendLine("Return ONLY the JSON object described in the system message.");
        return sb.ToString();
    }

    /// <summary>Adds a synthesized field only when it (a) carries a value, (b) cites one of the pages we
    /// actually fetched, and (c) — for copy-from-source fields — appears verbatim in the fetched text.
    /// A value that fails any check is dropped, never fabricated into the draft.</summary>
    private static void AddField(
        List<BlueprintResearchField> fields, JsonElement root, string name, string[] urls, string combinedText,
        bool requireInText, Func<string, string>? normalize = null)
    {
        var value = ReadString(root, name);
        if (string.IsNullOrWhiteSpace(value))
            return;

        var citedSource = ReadString(root, name + "_source");
        var citedUrl = citedSource is null ? null : Array.Find(urls, u => UrlMatches(u, citedSource));
        if (citedUrl is null)
            return; // no citation to a provided page → treat as unsourced (anti-fabrication)

        value = value.Trim();
        if (normalize is not null)
        {
            value = normalize(value);
            if (string.IsNullOrWhiteSpace(value))
                return;
        }

        if (requireInText && !combinedText.Contains(value, StringComparison.OrdinalIgnoreCase))
            return; // a copy-from-source fact the fetched pages don't actually contain → drop it

        fields.Add(new BlueprintResearchField(name, value, citedUrl));
    }

    private static bool UrlMatches(string fetched, string cited)
    {
        static string N(string s) => s.Trim().TrimEnd('/').ToLowerInvariant();
        var f = N(fetched);
        var c = N(cited);
        return c.Length >= 15 && (f == c || f.StartsWith(c, StringComparison.Ordinal) || c.StartsWith(f, StringComparison.Ordinal));
    }

    /// <summary>Finds the first balanced <c>{…}</c> object in the model's reply (it may wrap the JSON in
    /// prose or a code fence despite the instruction). Returns null if none parses.</summary>
    private static string? ExtractJsonObject(string s)
    {
        var start = s.IndexOf('{');
        if (start < 0)
            return null;

        var depth = 0;
        for (var i = start; i < s.Length; i++)
        {
            if (s[i] == '{')
                depth++;
            else if (s[i] == '}' && --depth == 0)
                return s[start..(i + 1)];
        }
        return null;
    }

    private static bool? ReadBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? e.GetBoolean()
            : null;

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static string NormalizePort(string v)
    {
        var m = Regex.Match(v, @"\d{2,5}");
        return m.Success ? m.Value : string.Empty;
    }

    private static string NormalizeDigits(string v)
    {
        var m = Regex.Match(v, @"\d{3,8}");
        return m.Success ? m.Value : string.Empty;
    }

    // KGSM defines exactly these launch-time variables; everything else in a $token is a hallucination
    // that resolves to an empty string at runtime and breaks the boot.
    private static readonly Regex DollarToken = new(@"\$\{?([A-Za-z_][A-Za-z0-9_]*)\}?", RegexOptions.Compiled);

    /// <summary>Guards <c>executable_arguments</c>: if the value references any <c>$VARIABLE</c> other than
    /// the real <c>$instance_*</c> placeholders (the model sometimes invents <c>$SERVER_PORT</c> etc., which
    /// KGSM never substitutes → they resolve to empty and the server fails to boot), the whole argument
    /// string is unreliable, so it is dropped (returns empty → <see cref="AddField"/> omits the field, and
    /// the server boots on its defaults instead of with broken flags). A clean value passes through
    /// unchanged.</summary>
    private static string RejectForeignPlaceholders(string v)
    {
        foreach (Match m in DollarToken.Matches(v))
        {
            if (!m.Groups[1].Value.StartsWith("instance_", StringComparison.Ordinal))
                return string.Empty;
        }
        return v;
    }
}
