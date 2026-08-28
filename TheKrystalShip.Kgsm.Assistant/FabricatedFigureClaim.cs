using System.Text.RegularExpressions;

namespace TheKrystalShip.Kgsm.Assistant;

/// <summary>
/// Detects a reply that reports a figure no tool gave it this turn.
/// </summary>
/// <remarks>
/// <para>
/// The model retypes measured values into its prose, and it does not always retype them correctly.
/// Measured on <c>gemma4:12b</c> against an instance configured <c>8211/udp|27015/udp</c>: asked
/// "what port is Ketchup on?" it answers correctly one time in three and otherwise reports
/// <c>21075</c>, <c>17015</c> or <c>21015</c> — values that appear in no configuration on the host.
/// The tool call is right, its result is right, and the digits change on the way into the sentence.
/// Nothing downstream can tell such an answer from a correct one: a wrong port reads exactly as
/// confidently as the right one, and the reader is the only check.
/// </para>
/// <para>
/// So the test is not whether a figure is plausible but whether it was <b>in front of the model</b>:
/// every substantial run of digits in the reply has to appear somewhere in what the turn was given
/// (<see cref="MeasuredValues"/>) — the tool output, the request, or the injected lists.
/// </para>
/// <para>
/// <b>Only runs on a turn that called a tool.</b> A turn that called none is answering from the
/// model's own knowledge, where "Minecraft uses 25565 by default" is a fair answer to a general
/// question and the prompt already requires it to be labelled as such. Deciding this from the turn
/// rather than the prose is the same construction <see cref="UnbackedActionClaim"/> uses.
/// </para>
/// </remarks>
public static partial class FabricatedFigureClaim
{
    /// <summary>
    /// The shortest run of digits worth checking.
    /// </summary>
    /// <remarks>
    /// Four, because that is where measured values start and derived ones stop. Ports, versions and
    /// byte counts run four digits or more; the numbers a reply computes rather than copies — how
    /// many servers are up, a percentage, "the third backup" — are one to three, and checking those
    /// would flag arithmetic the model is entitled to do. The failure this exists for is a
    /// five-digit port, and the cost of missing a fabricated <c>42</c> is far below the cost of
    /// arguing with every count in every fleet answer.
    /// </remarks>
    private const int SignificantDigits = 4;

    /// <summary>
    /// Appended when the figures are still unbacked after the turn was given another attempt. It does
    /// not name a correct value because none is known — the point is that the reply states one the
    /// tools did not, and saying which is wrong is the reader's only way to catch it.
    /// </summary>
    public const string Correction =
        "\n\n**Correction — one or more figures above are not from any tool result in this turn.** "
        + "I may have mistyped a measured value. Ask me again and I will read it back fresh rather "
        + "than repeat it.";

    /// <summary>
    /// Shown in place of <see cref="Correction"/> when the turn re-prompts itself, so the attempt
    /// that follows reads as a second attempt rather than as more of the first.
    /// </summary>
    public const string RetryNotice =
        "\n\n*(Correction — I quoted a figure that was not in the tool's answer. Reading it again.)*\n\n";

    /// <summary>
    /// The model-facing half: names the figures that are unbacked and requires the reply to be
    /// rebuilt from the tool output rather than from what it just wrote. The values are quoted back
    /// because a nudge that only says "a number was wrong" leaves the model to guess which, and it
    /// guesses the one it is most confident about — the one it invented.
    /// </summary>
    public static string NudgeFor(IReadOnlyList<string> figures) =>
        $"Your last reply reported {Describe(figures)}, which appear nowhere in the tool results you "
        + "were given this turn. You are reading them from memory rather than from the answer in "
        + "front of you. Write the reply again, copying every number straight out of the tool result "
        + "digit for digit; if the tool did not report a figure, do not state one.";

    private static string Describe(IReadOnlyList<string> figures) =>
        figures.Count == 1
            ? $"the figure {figures[0]}"
            : "the figures " + string.Join(", ", figures);

    /// <summary>Whether <paramref name="reply"/> already carries the correction, which must never be
    /// appended twice.</summary>
    public static bool CorrectionIsPresentIn(string? reply) =>
        reply is not null && reply.Contains(Correction, StringComparison.Ordinal);

    /// <summary>A maximal run of digits — bounded so it cannot match part of a longer number.</summary>
    [GeneratedRegex(@"(?<!\d)\d+(?!\d)")]
    private static partial Regex DigitRun();

    /// <summary>
    /// The substantial figures in <paramref name="reply"/> that appear nowhere in
    /// <paramref name="given"/>, in the order they are stated and without repeats. Empty when every
    /// figure is backed, which is the overwhelmingly common case.
    /// </summary>
    /// <remarks>
    /// Separators are removed from both sides before matching, so a reply that writes <c>27,015</c>
    /// is backed by a tool that printed <c>27015</c>. Matching is on a maximal digit run at both
    /// ends: without that, <c>2701</c> would be "found" inside <c>27015</c> and a truncated port
    /// would pass as measured.
    /// <para>
    /// <b>An empty <paramref name="given"/> flags nothing.</b> It means the turn recorded nothing,
    /// which is a fault in the ledger and not evidence that every figure in the reply was invented —
    /// and a check that fails closed here would contradict every correct answer on the way to
    /// reporting its own bug.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> UnbackedIn(string? reply, string? given)
    {
        if (string.IsNullOrWhiteSpace(reply) || string.IsNullOrWhiteSpace(given))
            return [];

        var haystack = Strip(given);
        var backed = DigitRun().Matches(haystack).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);

        var unbacked = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match match in DigitRun().Matches(Strip(reply)))
        {
            var figure = match.Value;
            if (figure.Length < SignificantDigits || backed.Contains(figure) || !seen.Add(figure))
                continue;

            unbacked.Add(figure);
        }

        return unbacked;
    }

    /// <summary>
    /// Removes the separators a figure is written with, so the same number reads the same on both
    /// sides. Only the characters that sit INSIDE a number are removed — a decimal point is left
    /// alone, because <c>8.2</c> and <c>82</c> are different figures and joining them would make a
    /// fabricated one look backed.
    /// </summary>
    private static string Strip(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : SeparatorInsideNumber().Replace(text, string.Empty);

    [GeneratedRegex(@"(?<=\d)[,_ ](?=\d\d\d(?!\d))")]
    private static partial Regex SeparatorInsideNumber();
}
