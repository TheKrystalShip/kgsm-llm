using System.Text;

using Microsoft.Extensions.Logging;

using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Blueprints;

/// <summary>
/// The agentic <see cref="IBlueprintResearch"/>: a bounded research sub-loop that drives its OWN
/// <c>search</c> + <c>fetch_url</c> tool calls to gather the pages describing how to run a game's native
/// Linux server, then hands them to <see cref="IBlueprintSynthesizer"/> for sourced extraction. It exists
/// because a single fixed "one query → top-3 → extract" pass mis-selects sources (it lands on a community
/// Docker wrapper instead of the official native doc), and no extractor can produce a fact that was never
/// fetched. The division of labour is deliberate: the MODEL does source selection (run several targeted
/// searches, follow the authoritative pages, look past Docker wrappers) — the thing it is good at — while
/// the synthesizer does the sourced, anti-fabrication-guarded extraction over what it fetched.
/// <para>
/// Deliberately bounded (a handful of model rounds and fetches) — research is a background step, not an
/// open crawl. It degrades gracefully: any loop failure, or a run that gathers nothing, falls back to the
/// fixed <see cref="BlueprintResearchAggregator"/> pass; and synthesis over the gathered pages itself
/// falls back to the deterministic extractor. Never throws (no underlying step does; only cancellation
/// propagates).
/// </para>
/// </summary>
public sealed class AgenticBlueprintResearch : IBlueprintResearch
{
    private const int MaxRounds = 6;        // model turns before we stop and extract from what we have
    private const int MaxFetches = 6;       // page reads budget across the whole pass
    private const int MaxResultsShown = 5;  // search hits surfaced to the model per query
    private const int MaxCharsPerPage = 8000;   // kept per page for the final synthesis
    private const int FeedbackChars = 5000;     // fed back to the model per fetch (context control)

    private static readonly Tool SearchTool = new("search");
    private static readonly Tool FetchTool = new("fetch_url");

    private static readonly IReadOnlyList<LlmToolDefinition> Tools =
    [
        LlmToolDefinition.Create(SearchTool,
            "Search the web for pages about hosting this game's dedicated server. Returns result titles and URLs.",
            new LlmToolParameter("query", "The web search query.")),
        LlmToolDefinition.Create(FetchTool,
            "Fetch and read the full text of ONE web page by URL — to find the native launch script, its " +
            "headless arguments, the port(s), and the SteamCMD dedicated-server app id.",
            new LlmToolParameter("url", "The absolute http(s) URL to read.")),
    ];

    private const string SystemPrompt =
        "You are a research agent. Find everything needed to run the NATIVE Linux dedicated server for a " +
        "given game, so another system can write an install config. You have two tools: search(query) and " +
        "fetch_url(url).\n\n" +
        "Gather, from real pages:\n" +
        "- the native Linux server LAUNCH script/binary (the file run directly on Linux — NOT a Docker " +
        "entrypoint like entry.sh; look past containerization to the script the game itself ships)\n" +
        "- the exact command-line ARGUMENTS to launch it headless (non-interactively)\n" +
        "- the primary network PORT(s)\n" +
        "- the SteamCMD DEDICATED-SERVER app id (from a `steamcmd +app_update <id>` command), if it's a Steam game\n" +
        "- a server-ready LOG LINE, if documented\n\n" +
        "How to work:\n" +
        "- Run SEVERAL targeted searches, not one: the official site/wiki, the Steam page, \"how to run <game> " +
        "dedicated server linux\", the specific launch command or script name.\n" +
        "- fetch_url the most AUTHORITATIVE pages (official docs, the game's own wiki, the Steam store/community). " +
        "Prefer official sources; treat community Docker wrappers as a last resort.\n" +
        "- When a page names a launch script or a steamcmd command, fetch it — that is the high-value source.\n" +
        "- Stop once you have the launch script + args + port (+ app id if it's a Steam game), or you have " +
        "exhausted good sources. When done, reply with a short plain-text summary — another system reads the " +
        "pages you fetched to extract the exact fields, so your job is to FETCH the right pages.";

    private readonly ILlmClient _llm;
    private readonly IWebSearch _webSearch;
    private readonly IWebFetch _webFetch;
    private readonly IBlueprintSynthesizer _synthesizer;
    private readonly BlueprintResearchAggregator _fixed;
    private readonly ILogger<AgenticBlueprintResearch> _logger;

    public AgenticBlueprintResearch(
        ILlmClient llm, IWebSearch webSearch, IWebFetch webFetch, IBlueprintSynthesizer synthesizer,
        BlueprintResearchAggregator fixedFallback, ILogger<AgenticBlueprintResearch> logger)
    {
        _llm = llm;
        _webSearch = webSearch;
        _webFetch = webFetch;
        _synthesizer = synthesizer;
        _fixed = fixedFallback;
        _logger = logger;
    }

    public async Task<BlueprintResearchFindings> ResearchAsync(string game, CancellationToken cancellationToken = default)
    {
        game = game.Trim();

        List<(string Url, string Text)> pages;
        try
        {
            pages = await GatherAsync(game, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Agentic blueprint research failed for \"{Game}\"; using the fixed research pass", game);
            return await _fixed.ResearchAsync(game, cancellationToken);
        }

        // Nothing gathered (model unavailable, fetched nothing) — the fixed one-query pass is the fallback.
        if (pages.Count == 0)
            return await _fixed.ResearchAsync(game, cancellationToken);

        _logger.LogInformation("Agentic research gathered {Count} page(s) for \"{Game}\"", pages.Count, game);
        var synthesized = await _synthesizer.SynthesizeAsync(game, pages, cancellationToken);
        return synthesized ?? BlueprintFactExtractor.Extract(game, pages);
    }

    private async Task<List<(string Url, string Text)>> GatherAsync(string game, CancellationToken cancellationToken)
    {
        var pages = new List<(string Url, string Text)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var messages = new List<LlmMessage>
        {
            LlmMessage.System(SystemPrompt),
            LlmMessage.User($"Game: {game}\n\nFind how to run its native Linux dedicated server. Start searching."),
        };
        var fetches = 0;

        for (var round = 0; round < MaxRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = await _llm.ChatAsync(messages, Tools, think: false, cancellationToken);
            if (response.IsFailure || response.Value is null)
                break;

            var calls = response.Value.ToolCalls;
            if (calls is null || calls.Count == 0)
                break; // the model summarized / has nothing more to do

            messages.Add(LlmMessage.AssistantToolCalls(calls));

            foreach (var call in calls)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (call.Name == SearchTool)
                {
                    var query = call.Arg("query");
                    var result = await _webSearch.SearchAsync(string.IsNullOrWhiteSpace(query) ? game : query!, cancellationToken);
                    messages.Add(LlmMessage.Tool(SearchTool, FormatSearch(result)));
                }
                else if (call.Name == FetchTool)
                {
                    if (fetches >= MaxFetches)
                    {
                        messages.Add(LlmMessage.Tool(FetchTool, "Fetch budget reached — summarize what you have and finish."));
                        continue;
                    }

                    var url = call.Arg("url");
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        messages.Add(LlmMessage.Tool(FetchTool, "No url provided."));
                        continue;
                    }

                    fetches++;
                    var fetch = await _webFetch.FetchAsync(url, cancellationToken);
                    if (fetch.IsSuccess && fetch.Value is not null)
                    {
                        if (seen.Add(fetch.Value.FinalUrl))
                            pages.Add((fetch.Value.FinalUrl, Cap(fetch.Value.Text, MaxCharsPerPage)));
                        messages.Add(LlmMessage.Tool(FetchTool, Cap(fetch.Value.Text, FeedbackChars)));
                    }
                    else
                    {
                        messages.Add(LlmMessage.Tool(FetchTool, $"Couldn't fetch: {fetch.Error}"));
                    }
                }
                else
                {
                    messages.Add(LlmMessage.Tool(call.Name, "Unknown tool — use search or fetch_url."));
                }
            }
        }

        return pages;
    }

    private static string FormatSearch(Result<IReadOnlyList<WebSearchHit>> result)
    {
        if (result.IsFailure || result.Value is null || result.Value.Count == 0)
            return "No results.";

        var sb = new StringBuilder("Results:\n");
        foreach (var hit in result.Value.Take(MaxResultsShown))
            sb.Append("- ").Append(hit.Title).Append(" — ").AppendLine(hit.Url);
        return sb.ToString();
    }

    private static string Cap(string text, int max) =>
        text.Length > max ? text[..max] : text;
}
