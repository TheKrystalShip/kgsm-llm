using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace TheKrystalShip.Kgsm.Assistant.Search;

/// <summary>
/// Restricts local retrieval to the game a query is about.
/// <para>
/// The docs corpus is organised per game (<c>.../games/&lt;game&gt;/&lt;doc&gt;.md</c>), but similarity
/// alone is game-blind: generic operator phrasing ("my server is lagging", "nobody can join")
/// matches ANY game's troubleshooting prose, so a question about a game with no documents is
/// answered from a different game's — and, because a local hit above the aggregator's floor
/// suppresses the web fallback entirely, that wrong answer also prevents the right one.
/// </para>
/// <para>
/// So a query naming exactly one known game admits only that game's documents plus the
/// game-neutral ones; a question about an undocumented game then finds nothing locally and falls
/// through to the web, which is the honest outcome. Naming no game (or several) leaves the whole
/// corpus eligible and lets similarity decide, since there is nothing better to go on.
/// </para>
/// </summary>
public static class GameScope
{
    /// <summary>Path segment marking the per-game subtree of the corpus.</summary>
    private const string GamesSegment = "games";

    /// <summary>Separators tolerated between the characters of a game name.</summary>
    private const string NameSeparators = @"[\s._-]*";

    /// <summary>Compiled matcher per game name; the vocabulary is small and long-lived.</summary>
    private static readonly ConcurrentDictionary<string, Regex> Matchers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The single game named in <paramref name="query"/>, or <see langword="null"/> when it names
    /// none, more than one, or the vocabulary is unavailable. Matching is whole-word and
    /// case-insensitive, so <c>killingfloor2</c> does not also match <c>killingfloor</c>.
    /// </summary>
    public static string? Resolve(string query, IReadOnlyCollection<string> gameNames)
    {
        if (string.IsNullOrWhiteSpace(query) || gameNames.Count == 0)
            return null;

        var mentions = new List<(string Name, int Start, int End)>();
        foreach (var name in gameNames)
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;

            foreach (Match m in Matchers.GetOrAdd(name, BuildMatcher).Matches(query))
                mentions.Add((name, m.Index, m.Index + m.Length));
        }

        string? found = null;
        foreach (var mention in mentions)
        {
            // One stretch of text is one mention, however many names match it: "killing floor 2"
            // matches both `killingfloor` and `killingfloor2`, and reading that as two games named
            // would make every longer name unresolvable. The widest match is the one meant.
            if (mentions.Any(other => !NameEquals(other.Name, mention.Name)
                    && other.Start <= mention.Start && other.End >= mention.End
                    && other.End - other.Start > mention.End - mention.Start))
                continue;

            // A second, different game makes the query ambiguous — scope nothing rather than guess.
            if (found is not null && !NameEquals(found, mention.Name))
                return null;

            found = mention.Name;
        }

        return found;
    }

    private static bool NameEquals(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A matcher for one game name that tolerates separators between its characters, because a
    /// blueprint name is one concatenated token (<c>projectzomboid</c>, <c>theforest</c>) while a
    /// person types the words apart. Requiring the exact token leaves scoping inert for every such
    /// game — the query names a game, nothing resolves, and the whole corpus stays eligible.
    /// <para>
    /// The word boundaries are what keep this precise: after matching <c>killingfloor</c> the
    /// trailing <c>\b</c> fails against the <c>2</c> of <c>killingfloor2</c>, so the shorter name
    /// still does not swallow the longer one.
    /// </para>
    /// </summary>
    private static Regex BuildMatcher(string name)
    {
        var pattern = new StringBuilder(@"\b");
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0)
                pattern.Append(NameSeparators);
            pattern.Append(Regex.Escape(name[i].ToString()));
        }

        pattern.Append(@"\b");
        return new Regex(pattern.ToString(),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }

    /// <summary>
    /// Whether a chunk from <paramref name="sourcePath"/> may answer a query scoped to
    /// <paramref name="scope"/>. A document outside the per-game subtree is game-neutral and always
    /// admitted; one inside it is admitted only for its own game. A null scope admits everything.
    /// </summary>
    public static bool Admits(string sourcePath, string? scope)
    {
        if (scope is null)
            return true;

        var game = GameOf(sourcePath);
        return game is null || string.Equals(game, scope, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The game a document belongs to — the segment after <c>games/</c> — or <see langword="null"/>
    /// when it sits outside the per-game subtree.
    /// </summary>
    public static string? GameOf(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return null;

        // The game is a DIRECTORY under `games/`, so the match needs a further segment beyond it —
        // that is what keeps an index page sitting directly in `games/` game-neutral.
        var segments = sourcePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length - 2; i++)
        {
            if (string.Equals(segments[i], GamesSegment, StringComparison.OrdinalIgnoreCase))
                return segments[i + 1];
        }

        return null;
    }
}
