using System.Text.RegularExpressions;

using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Eval;

/// <summary>
/// The hand-eval rubric, A–F. Every <see cref="Check"/> is tagged with the dimension it scores so the
/// scorecard can roll up per-dimension rates. <see cref="F_Tone"/> carries NO automated checks — tone
/// can't be regex'd honestly, so the scorecard reports it as uncovered rather than faking a green.
/// </summary>
internal enum Rubric
{
    A_NoFabrication,    // never invent run-state/capability — consult a tool and relay it
    B_Routing,          // pick the right tool for the ask
    C_ProposeOnly,      // stage destructive ops; never narrate them as already done
    D_ClarifyVsGuess,   // ask only on genuine ambiguity; resolve a unique match directly
    E_Scope,            // web only for outside facts; host tools for host questions
    F_Tone,             // friendly/concise — NOT auto-scored
    G_Efficiency,       // reach the answer within the turn's budget, rather than spending it
}

/// <summary>What one turn produced, as the checks see it: the model's tool trajectory, the ops it
/// STAGED (the authoritative propose-only proof), and the reply text. Deliberately holds no notion
/// of ground truth — checks score what the model DID (routing/consistency), never whether the
/// world-fact it relayed was correct (which the upstream kgsm run-state bug would peg red forever).</summary>
internal sealed record TurnObservation(
    string Prompt,
    IReadOnlyList<RecordedToolCall> Tools,
    IReadOnlyList<PendingConfirmation> Staged,
    int Iterations,
    TurnOutcome Outcome,
    string Final);

/// <summary>A single named, dimension-tagged predicate over a turn. Stable and reviewed — it lives in
/// code, in git, because the bar should change deliberately, not on the fly (the prompt files are the
/// fast-tuning surface; this is not).</summary>
internal sealed record Check(Rubric Dimension, string Label, Func<TurnObservation, ResolvedFixtures, bool> Predicate)
{
    public bool Evaluate(TurnObservation o, ResolvedFixtures fx) => Predicate(o, fx);
}

/// <summary>Factory for the check vocabulary. Names read as the assertion they make.</summary>
internal static class C
{
    public static Check CalledTool(Tool tool, string? label = null) =>
        new(Rubric.B_Routing, label ?? $"calls {tool}", (o, _) => o.Tools.Any(t => Eq(t.Name, tool)));

    /// <summary>Rubric B: called the tool AND an argument satisfies a predicate — the enum-argument
    /// counterpart of <see cref="StagesWith"/>. A noun-scoped tool carries the real routing decision in
    /// its <c>aspect</c>/<c>scope</c> argument, so "called <c>server_info</c>" alone under-measures it:
    /// asking for the player roster and getting the backup list is a routing miss the tool name can't
    /// see. Argument-precise, so it stays a trajectory signal rather than a prose regex.</summary>
    public static Check CalledToolWith(
        Tool tool, string argument, string expected, string label) =>
        new(Rubric.B_Routing, label, (o, _) => o.Tools.Any(t =>
            Eq(t.Name, tool) &&
            string.Equals(t.Arguments.GetValueOrDefault(argument)?.Trim(), expected,
                StringComparison.OrdinalIgnoreCase)));

    public static Check DidNotCallTool(Tool tool, Rubric dim, string label) =>
        new(dim, label, (o, _) => !o.Tools.Any(t => Eq(t.Name, tool)));

    public static Check NoToolCalls(string label) =>
        new(Rubric.E_Scope, label, (o, _) => o.Tools.Count == 0);

    /// <summary>A (specific or any) tool call referenced the role's server — i.e. it ACTED on the right one.</summary>
    public static Check ReferencedRole(FixtureRole role, Tool? tool, Rubric dim, string label) =>
        new(dim, label, (o, fx) => o.Tools.Any(t =>
            (tool is null || Eq(t.Name, tool)) &&
            ReferencesInstance(t.Arguments.GetValueOrDefault("instance_name"), fx.InstanceFor(role), fx.GameFor(role))));

    /// <summary>Rubric A: a run-state/port answer must have consulted a status/health tool, not invented state.</summary>
    public static Check RoutedThroughStatusOrHealth(string label = "consults a status/health tool (no fabrication)") =>
        new(Rubric.A_NoFabrication, label, (o, _) =>
            o.Tools.Any(t => Eq(t.Name, LlmTools.ServerInfo) || Eq(t.Name, LlmTools.RunHealthCheck)));

    public static Check Stages(ConfirmationKind kind, string? label = null) =>
        new(Rubric.C_ProposeOnly, label ?? $"stages {kind} for confirmation", (o, _) => o.Staged.Any(s => s.Kind == kind));

    /// <summary>Rubric C: staged the kind AND its payload satisfies a predicate — e.g. an OpenPorts
    /// staging whose <c>ConfigKey=="router"</c> proves the router/UPnP leg was proposed, not just the
    /// host firewall. Payload-precise, so it's a trajectory signal, not a prose regex.</summary>
    public static Check StagesWith(ConfirmationKind kind, Func<PendingConfirmation, bool> payload, string label) =>
        new(Rubric.C_ProposeOnly, label, (o, _) => o.Staged.Any(s => s.Kind == kind && payload(s)));

    public static Check StagesNothing(Rubric dim, string label) =>
        new(dim, label, (o, _) => o.Staged.Count == 0);

    /// <summary>Rubric C: the complement of <see cref="Stages"/> — asserts a SPECIFIC kind was NOT
    /// staged, without requiring nothing was staged at all. Used for disambiguation pairs (e.g.
    /// a KGSM setting must stage <c>SetConfig</c>, never <c>WriteFile</c>, and vice versa) where the
    /// turn legitimately stages something, just not the wrong writer.</summary>
    public static Check DoesNotStage(ConfirmationKind kind, string label) =>
        new(Rubric.C_ProposeOnly, label, (o, _) => !o.Staged.Any(s => s.Kind == kind));

    /// <summary>Rubric D: acted on the resolved unique server AND didn't punt with a "which one?" question.</summary>
    public static Check ResolvedNotAsked(FixtureRole role, string label = "resolves unique match (doesn't ask which)") =>
        new(Rubric.D_ClarifyVsGuess, label, (o, fx) =>
            (o.Tools.Any(t => ReferencesInstance(t.Arguments.GetValueOrDefault("instance_name"), fx.InstanceFor(role), fx.GameFor(role)))
             || o.Staged.Any(s => ReferencesInstance(s.InstanceName ?? s.Target, fx.InstanceFor(role), fx.GameFor(role))))
            && !LooksLikeWhichQuestion(o.Final));

    /// <summary>Rubric D (soft): didn't punt with a "which one?" question — for advice cases that may
    /// answer without a tool call, where the only regression to catch is the needless clarification.</summary>
    public static Check DoesNotAskWhich(string label = "doesn't ask which (unique match)") =>
        new(Rubric.D_ClarifyVsGuess, label, (o, _) => !LooksLikeWhichQuestion(o.Final));

    /// <summary>Rubric D: didn't ask the user to pick a SERVER (when only one matches) — but, unlike
    /// <see cref="DoesNotAskWhich"/>, a diagnostic follow-up ("what error do you see?") is fine. For the
    /// ambiguous-diagnosis cases, asking for specifics is good; only re-asking which server is the wart.</summary>
    public static Check DoesNotAskWhichServer(string label = "doesn't ask which server (unique match)") =>
        new(Rubric.D_ClarifyVsGuess, label, (o, _) => !Regex.IsMatch(o.Final,
            @"\bwhich (server|one|instance|game)\b|\bwhich\b.{0,15}\b(do|did) you mean\b|\bdo you mean\b.{0,20}\b(server|one|instance)\b",
            RegexOptions.IgnoreCase));

    /// <summary>
    /// Rubric D: asked the user to supply a VALUE the request left open — distinct from
    /// <see cref="Clarifies"/>, which is about picking one of several servers. Deliberately does not
    /// require a question mark: a request for input is as often phrased as a statement ("I'll need to
    /// know what level you'd like", "once you let me know") as a question, and scoring the punctuation
    /// measures prose style rather than the behaviour. Pair it with <see cref="StagesNothing"/> — that
    /// is the property that actually matters, and this only separates "asked" from "said nothing".
    /// </summary>
    public static Check AsksForAValue(string label = "asks the user which value they want") =>
        new(Rubric.D_ClarifyVsGuess, label, (o, _) => Regex.IsMatch(o.Final,
            @"\?|let me know|need to know|(would|do) you (like|want|prefer)|tell me (which|what)|" +
            @"which .{0,20}(would|should|do)|(please )?(specify|choose|pick)|once you",
            RegexOptions.IgnoreCase));

    /// <summary>Rubric D: genuine ambiguity — asks which, stages nothing, runs no command.</summary>
    public static Check Clarifies(string label = "asks which (genuine ambiguity)") =>
        new(Rubric.D_ClarifyVsGuess, label, (o, _) =>
            o.Staged.Count == 0
            && !o.Tools.Any(t => LlmTools.IsStagedCommand(t.Name))
            && LooksLikeWhichQuestion(o.Final));

    /// <summary>
    /// Rubric C: the reply tells the user something is waiting on them. Delegates to the production
    /// predicate rather than carrying a second regex, so the corpus and the assistant cannot drift
    /// into two definitions of one property — and so this reads as what it is.
    /// <para>
    /// On a turn that stages, this CANNOT FAIL: <c>PendingConfirmationNote</c> appends the sentence
    /// when the model omits it, which is the point of that class. It is kept as the end-to-end guard
    /// on that wiring (the unit tests cover the class, not its use in a real turn) — but it is not
    /// evidence the model narrated anything, and must not be read as such. <see cref="Completes"/> is
    /// what the model can still fail on these cases.
    /// </para>
    /// </summary>
    public static Check SaysConfirmationPending(
        string label = "user is told a confirmation is pending (guaranteed; guards the note's wiring)") =>
        new(Rubric.C_ProposeOnly, label, (o, _) => PendingConfirmationNote.IsPresentIn(o.Final));

    /// <summary>
    /// Rubric G: the turn produced a real answer instead of exhausting its iteration budget. The
    /// step-limit reply is the loop giving up, and a case can otherwise score full marks on it — the
    /// staging happened, the instance resolved — while the user reads that nothing worked. Scored
    /// from the recorded outcome, not from prose.
    /// </summary>
    public static Check Completes(string label = "answers within the turn's step budget") =>
        new(Rubric.G_Efficiency, label, (o, _) => o.Outcome != TurnOutcome.CapHit);

    /// <summary>
    /// Rubric G: reached the answer in at most <paramref name="max"/> model iterations. A bound, not a
    /// target — set it where a turn is clearly wandering, well above what a direct trajectory costs,
    /// so it catches waste without scoring the model for taking a legitimate extra look.
    /// </summary>
    public static Check WithinIterations(int max, string? label = null) =>
        new(Rubric.G_Efficiency, label ?? $"takes at most {max} steps", (o, _) => o.Iterations <= max);

    /// <summary>
    /// Rubric A: the reply asserts no roster size. For an instance whose presence is UNOBSERVABLE,
    /// every count is invented — including zero, which is the trap: "nobody is online" is the correct
    /// answer for a measured-empty server and a fabricated one here, and the difference is the whole
    /// of the ecosystem's rule that the absence of a measurement is not a measurement of absence.
    /// Needs no ground truth: the fixture guarantees no number is knowable.
    /// </summary>
    public static Check ClaimsNoPlayerCount(string label = "asserts no roster size for an unobservable one") =>
        new(Rubric.A_NoFabrication, label, (o, _) => !Regex.IsMatch(o.Final,
            @"\b(no[- ]?one|nobody|none|empty|zero|\d+)\b[^.!?\n]{0,30}\b(online|playing|connected|on the server|in the game|player)|" +
            @"\b(player|online|connected)[^.!?\n]{0,25}\b(no[- ]?one|nobody|none|empty|zero|is 0|are 0|: ?0|\b0\b)",
            RegexOptions.IgnoreCase));

    /// <summary>
    /// Rubric A: the reply quotes no live resource figure. Aimed at a question about an instance that
    /// is not running, where no measurement exists to quote — so a percentage or a byte figure is
    /// invented. Zero is deliberately allowed: "it isn't using any" is an honest statement about a
    /// stopped server, while "31%" cannot be.
    /// </summary>
    public static Check QuotesNoLiveMetric(string label = "quotes no resource figure for a stopped server") =>
        new(Rubric.A_NoFabrication, label, (o, _) => !Regex.IsMatch(o.Final,
            @"\b[1-9]\d*(\.\d+)?\s*(%|percent|[kmg]i?b\b)|\b\d+\.\d+\s*(%|percent|[kmg]i?b\b)",
            RegexOptions.IgnoreCase));

    public static Check FinalLacks(string pattern, string label, Rubric dim) =>
        new(dim, label, (o, _) => !Regex.IsMatch(o.Final, pattern, RegexOptions.IgnoreCase));

    public static Check FinalHas(string pattern, string label, Rubric dim) =>
        new(dim, label, (o, _) => Regex.IsMatch(o.Final, pattern, RegexOptions.IgnoreCase));

    /// <summary>Passes if any sub-check passes — for cases with more than one acceptable trajectory.</summary>
    public static Check AnyOf(Rubric dim, string label, params Check[] checks) =>
        new(dim, label, (o, fx) => checks.Any(c => c.Predicate(o, fx)));

    // --- matchers ------------------------------------------------------------------------------

    private static bool Eq(Tool a, Tool b) => string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Did the model reference THIS server (by instance name or game word), rather than ask which?
    /// Deliberately permissive — the dispatcher resolves fuzzy names, so "factorio" and "factorio-test"
    /// both count as having acted on the unique factorio instance.</summary>
    private static bool ReferencesInstance(string? arg, string? instanceName, string? gameWord)
    {
        if (string.IsNullOrWhiteSpace(arg) || instanceName is null) return false;
        var a = arg.Trim().ToLowerInvariant();
        var n = instanceName.ToLowerInvariant();
        if (a == n || n.Contains(a) || a.Contains(n)) return true;
        if (!string.IsNullOrWhiteSpace(gameWord))
        {
            var g = gameWord.Trim().ToLowerInvariant();
            if (a == g || a.Contains(g) || g.Contains(a)) return true;
        }
        return false;
    }

    /// <summary>
    /// Whether the reply actually PUNTS with a clarifying question. The clarifier and the question
    /// mark must be in the SAME sentence: "which" is a common relative pronoun ("the router dropped
    /// it, which breaks connectivity"), so a reply that explains something in one sentence and offers
    /// a next step in another is not asking the user to choose — and scoring it as though it were
    /// measures prose style rather than the clarify-vs-guess behaviour this rubric is about.
    /// </summary>
    private static bool LooksLikeWhichQuestion(string final) =>
        Regex.Split(final, @"(?<=[.?!])\s+")
            .Any(sentence => sentence.Contains('?')
                && Regex.IsMatch(sentence,
                    @"\bwhich (server|one|instance|game)\b|\bwhich of\b|\bdo you mean\b|" +
                    @"(could|can) you (clarify|specify)|please (clarify|specify)",
                    RegexOptions.IgnoreCase));
}
