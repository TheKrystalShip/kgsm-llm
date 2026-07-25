using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;

using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Blueprints;

/// <summary>
/// Model-driven blueprint repair (<see cref="IBlueprintRepair"/>): one LLM call that reads the REAL install
/// directory, the game's shipped launch scripts, and the boot LOG of a failed attempt, and proposes
/// corrected launch fields. This is the capable counterpart to a blind retry — the web research pass
/// guesses the executable from a wiki, whereas repair reads it off disk (the true filename is right there)
/// and reads the server's own error to fix the arguments. The anti-fabrication discipline is the same as
/// synthesis, but the checks are stronger BECAUSE the evidence is ground truth: a proposed
/// <c>executable_file</c> is kept only if it actually appears in the install tree (a file that is not on
/// disk cannot be the executable), and the foreign-<c>$VARIABLE</c> guard is reused verbatim.
/// <para>
/// Never throws (the contract) — any failure (unconfigured model, transport error, unparseable reply, a
/// proposal that changes nothing) returns <see langword="null"/>, and the pipeline stops retrying rather
/// than looping on an identical draft.
/// </para>
/// </summary>
public sealed class LlmBlueprintRepair : IBlueprintRepair
{
    private const int MaxTreeChars = 6000;      // rendered install listing budget
    private const int MaxScriptsChars = 8000;   // shipped launch-script text budget
    private const int MaxLogChars = 4000;       // boot-log tail budget

    private const string SystemPrompt =
        "You are fixing a NATIVE Linux dedicated game-server launch config that was just tried and FAILED " +
        "to boot. You are given: the launch fields that were tried, the REAL install directory tree (what " +
        "is actually on disk after the download), the text of any shipped launch SCRIPTS, and the server's " +
        "BOOT LOG from the failed attempt. Use this real evidence — not general knowledge — to propose " +
        "corrected fields. Output ONLY a single JSON object and nothing else.\n\n" +
        "The evidence is ground truth. The install tree shows the EXACT filename and location of every " +
        "file — do not propose an executable that is not in the tree. The boot log shows WHY it failed " +
        "(an unknown/rejected argument, a missing library, an interactive prompt it stalled on, a wrong " +
        "path). The launch scripts show how the game itself starts the server.\n\n" +
        "Rules:\n" +
        "- executable_file is the thing you RUN, relative to the install subdirectory. PREFER THE GAME'S " +
        "OWN LAUNCH SCRIPT (e.g. start_server.sh, _launch.sh, start_server_bepinex.sh) when the tree shows " +
        "one next to a raw binary — the script sets up the runtime (LD_LIBRARY_PATH, cd, env) the raw " +
        "binary needs, and running the raw binary directly usually fails to load its bundled libraries " +
        "(the boot log will often show exactly that). A Docker entrypoint (entry.sh, docker-entrypoint.sh) " +
        "is NOT the executable.\n" +
        "- Some servers run THROUGH an interpreter: a Java server (.jar) has executable_file \"java\" with " +
        "\"-jar <thefile>.jar\" in executable_arguments; a .NET server (.dll) has executable_file " +
        "\"dotnet\" with \"<thefile>.dll\" in executable_arguments. Pick the interpreter shape when the " +
        "tree shows a .jar/.dll and no native launch script.\n" +
        "- executable_subdirectory: if the executable lives in a subfolder of the install root (the tree " +
        "shows e.g. bin/x64/factorio), set it to that subfolder (\"bin/x64\") and put ONLY the filename in " +
        "executable_file. If the executable sits at the install root, set it to \"\" (empty string).\n" +
        "- executable_arguments must launch the server HEADLESS / non-interactively and be MINIMAL. If the " +
        "boot log shows an argument was rejected/unknown, fix or drop it (the scripts and log reveal the " +
        "correct flag names). If the log shows the server stalled at an interactive prompt, add the flag " +
        "that makes it non-interactive. Only these KGSM placeholders exist — $instance_level_name, " +
        "$instance_saves_dir, $instance_install_dir — NEVER write any other $VARIABLE ($SERVER_PORT, " +
        "$PORT, $SERVER_NAME, etc.): KGSM does not define them, they resolve to EMPTY and the boot fails. " +
        "For a value the docs leave to the user (port, name, password), write a concrete literal or omit " +
        "the optional flag. If the executable is a wrapper script that reads its own config, prefer EMPTY " +
        "arguments.\n" +
        "- UNITY SERVERS HIDE THEIR LOG. If the install tree shows a Unity engine server (a `*_Data/` " +
        "directory, `UnityPlayer.so`, or `*_Data/Managed/`), it writes its output to a private Player.log " +
        "the monitor CANNOT see — so an empty monitored boot log does NOT mean nothing happened. Add " +
        "`-logfile /dev/stdout` to the arguments so its startup output (including any error like 'Password " +
        "is too short' or a missing-argument message) reaches the monitored stdout. A Unity dedicated " +
        "server also needs `-nographics -batchmode` to run headless without a display — if those are " +
        "missing from the arguments, ADD them (a Unity server started without them tries to open a window, " +
        "fails on a headless host, and never binds its port). If the current executable is a TEMPLATE " +
        "wrapper script that ignores passed arguments (a vanilla `start_server.sh` with hard-coded example " +
        "flags), switch executable_file to the server BINARY directly (`*_server.x86_64`, `*Server.x86_64` " +
        "from the tree) so your arguments actually take effect.\n" +
        "- READINESS IS OBSERVED ON THE MONITORED STDOUT. The success check only sees what the server " +
        "prints to stdout (the 'monitored stdout' section of the boot log) or a bound network port. If the " +
        "current arguments REDIRECT the server's output to a file (e.g. `-logfile ServerLog.txt`, " +
        "`-logFile x`, `> file`, `--log-file x`), that hides the readiness line from the monitor — DROP " +
        "that redirect flag so the output goes to stdout where it can be seen. The boot log may include a " +
        "'game-written log file' section: that is proof the server ran and reached readiness, but its " +
        "output was invisible to the monitor — the fix is to stop redirecting, not to keep the redirect.\n" +
        "- startup_success_regex: many servers never bind a host-local port the monitor can detect (they " +
        "use a relay/NAT-punch), so a READY LOG LINE is the only usable signal. If the boot log (either " +
        "section) contains a clear 'server is ready / listening / started' line (e.g. 'Game ID', 'Server " +
        "started', 'Opened Steam server', 'Done!'), set this to a regex matching a stable substring of a " +
        "line ACTUALLY in the log. Prefer setting it whenever the port may not be detectable. Else null.\n" +
        "- ports: if the boot log or scripts reveal the actual port the server binds, set it to that PORT " +
        "NUMBER. Else null.\n" +
        "- For any field you are NOT changing, use null (keep the current value). Change ONLY what the " +
        "evidence tells you is wrong. If the evidence gives you no better idea than what was already " +
        "tried, return every field as null.\n\n" +
        "JSON shape (use exactly these keys):\n" +
        "{\n" +
        "  \"executable_file\": string|null,\n" +
        "  \"executable_subdirectory\": string|null,\n" +
        "  \"executable_arguments\": string|null,\n" +
        "  \"startup_success_regex\": string|null,\n" +
        "  \"ports\": string|null\n" +
        "}";

    private readonly ILlmClient _llm;
    private readonly ILogger<LlmBlueprintRepair> _logger;

    public LlmBlueprintRepair(ILlmClient llm, ILogger<LlmBlueprintRepair> logger)
    {
        _llm = llm;
        _logger = logger;
    }

    public async Task<BlueprintRepairProposal?> RepairAsync(
        BlueprintRepairContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var messages = new[]
            {
                LlmMessage.System(SystemPrompt),
                LlmMessage.User(BuildUserPrompt(context)),
            };

            var response = await _llm.ChatAsync(messages, tools: null, think: false, cancellationToken);
            if (response.IsFailure || string.IsNullOrWhiteSpace(response.Value?.Content))
                return null;

            var json = ExtractJsonObject(response.Value!.Content!);
            if (json is null)
                return null;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var exec = Clean(ReadString(root, "executable_file"));
            // A proposed executable must actually be on disk — the install tree is ground truth, so a
            // filename it does not contain is a hallucination, dropped rather than tried.
            if (exec is not null && !context.InstallTree.Contains(exec, StringComparison.OrdinalIgnoreCase))
                exec = null;

            var subdir = ReadString(root, "executable_subdirectory");
            // Empty string is a MEANINGFUL proposal here ("clear the subdir — binary is at the root"), so
            // it is distinct from null ("leave unchanged"). Only whitespace-that-is-not-empty is noise.
            string? subdirProposal = subdir is null ? null : subdir.Trim();

            var args = Clean(ReadString(root, "executable_arguments"));
            // Reuse the synthesizer's foreign-placeholder guard: an args string that references a
            // $VARIABLE KGSM does not define resolves to empty at runtime and breaks the boot — so a
            // proposal that reintroduces one is worse than useless. Drop it (keep the current args).
            if (args is not null && !string.IsNullOrEmpty(args) && HasForeignPlaceholder(args))
                args = null;

            var regex = Clean(ReadString(root, "startup_success_regex"));
            var ports = NormalizePort(Clean(ReadString(root, "ports")));

            // A proposal that changes nothing is the "no better idea" signal — return null so the caller
            // stops instead of re-running an identical draft.
            if (exec is null && subdirProposal is null && args is null && regex is null && ports is null)
                return null;

            return new BlueprintRepairProposal(exec, subdirProposal, args, regex, ports);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Blueprint repair synthesis failed for \"{Game}\"", context.Game);
            return null;
        }
    }

    private static string BuildUserPrompt(BlueprintRepairContext c)
    {
        var sb = new StringBuilder();
        sb.Append("Game: ").AppendLine(c.Game);
        sb.AppendLine().AppendLine("Launch fields just tried (they FAILED to boot):");
        sb.Append("  executable_file: ").AppendLine(Show(c.ExecutableFile));
        sb.Append("  executable_subdirectory: ").AppendLine(Show(c.ExecutableSubdirectory));
        sb.Append("  executable_arguments: ").AppendLine(Show(c.ExecutableArguments));
        sb.Append("  startup_success_regex: ").AppendLine(Show(c.StartupSuccessRegex));
        sb.Append("  ports: ").AppendLine(Show(c.Ports));

        sb.AppendLine();
        if (!c.InstallSucceeded)
            sb.Append("The test-install itself failed. Install error: ").AppendLine(Show(c.InstallError));
        else if (c.PortsReachable == false)
            sb.AppendLine("The server ran but never bound its configured port(s).");
        else
            sb.AppendLine("The server did not come up and answer within the timeout.");

        sb.AppendLine().AppendLine("REAL install directory tree (paths relative to the install root):");
        sb.AppendLine(string.IsNullOrWhiteSpace(c.InstallTree) ? "(the install produced no files)" : Cap(c.InstallTree, MaxTreeChars));

        sb.AppendLine().AppendLine("Shipped launch scripts found on disk:");
        sb.AppendLine(string.IsNullOrWhiteSpace(c.LaunchScripts) ? "(none found)" : Cap(c.LaunchScripts, MaxScriptsChars));

        sb.AppendLine().AppendLine("Boot log from the failed attempt (tail):");
        sb.AppendLine(string.IsNullOrWhiteSpace(c.BootLog) ? "(the server produced no log — nothing ran)" : Cap(c.BootLog, MaxLogChars));

        sb.AppendLine().AppendLine("Return ONLY the JSON object described in the system message. Change only what the evidence shows is wrong.");
        return sb.ToString();
    }

    private static string Show(string? v) => string.IsNullOrEmpty(v) ? "(empty)" : v;

    private static string? Clean(string? v)
    {
        if (v is null)
            return null;
        v = v.Trim();
        return v; // empty stays empty (a meaningful "clear this" for some fields); caller decides
    }

    private static readonly Regex DollarToken = new(@"\$\{?([A-Za-z_][A-Za-z0-9_]*)\}?", RegexOptions.Compiled);

    private static bool HasForeignPlaceholder(string v)
    {
        foreach (Match m in DollarToken.Matches(v))
        {
            if (!m.Groups[1].Value.StartsWith("instance_", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static string? NormalizePort(string? v)
    {
        if (string.IsNullOrWhiteSpace(v))
            return null;
        var m = Regex.Match(v, @"\d{2,5}");
        return m.Success ? m.Value : null;
    }

    /// <summary>Finds the first balanced <c>{…}</c> object in the model's reply. Returns null if none.</summary>
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

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static string Cap(string text, int max) => text.Length > max ? text[..max] : text;
}
