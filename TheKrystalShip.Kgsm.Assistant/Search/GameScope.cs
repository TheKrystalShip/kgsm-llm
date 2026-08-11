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

    /// <summary>
    /// The single game named in <paramref name="query"/>, or <see langword="null"/> when it names
    /// none, more than one, or the vocabulary is unavailable. Matching is whole-word and
    /// case-insensitive, so <c>killingfloor2</c> does not also match <c>killingfloor</c>.
    /// </summary>
    public static string? Resolve(string query, IReadOnlyCollection<string> gameNames)
    {
        if (string.IsNullOrWhiteSpace(query) || gameNames.Count == 0)
            return null;

        string? found = null;
        foreach (var name in gameNames)
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (!Regex.IsMatch(query, $@"\b{Regex.Escape(name)}\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                continue;

            // A second, different game makes the query ambiguous — scope nothing rather than guess.
            if (found is not null && !string.Equals(found, name, StringComparison.OrdinalIgnoreCase))
                return null;

            found = name;
        }

        return found;
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
