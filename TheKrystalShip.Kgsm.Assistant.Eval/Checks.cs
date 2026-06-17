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
    public static Check CalledTool(string tool, string? label = null) =>
        new(Rubric.B_Routing, label ?? $"calls {tool}", (o, _) => o.Tools.Any(t => Eq(t.Name, tool)));

    public static Check DidNotCallTool(string tool, Rubric dim, string label) =>
        new(dim, label, (o, _) => !o.Tools.Any(t => Eq(t.Name, tool)));

    public static Check NoToolCalls(string label) =>
        new(Rubric.E_Scope, label, (o, _) => o.Tools.Count == 0);

    /// <summary>A (specific or any) tool call referenced the role's server — i.e. it ACTED on the right one.</summary>
    public static Check ReferencedRole(FixtureRole role, string? tool, Rubric dim, string label) =>
        new(dim, label, (o, fx) => o.Tools.Any(t =>
            (tool is null || Eq(t.Name, tool)) &&
            ReferencesInstance(t.Arguments.GetValueOrDefault("instance_name"), fx.InstanceFor(role), fx.GameFor(role))));

    /// <summary>Rubric A: a run-state/port answer must have consulted a status/health tool, not invented state.</summary>
    public static Check RoutedThroughStatusOrHealth(string label = "consults a status/health tool (no fabrication)") =>
        new(Rubric.A_NoFabrication, label, (o, _) =>
            o.Tools.Any(t => Eq(t.Name, LlmTools.GetStatus) || Eq(t.Name, LlmTools.RunHealthCheck)));

    public static Check Stages(ConfirmationKind kind, string? label = null) =>
        new(Rubric.C_ProposeOnly, label ?? $"stages {kind} for confirmation", (o, _) => o.Staged.Any(s => s.Kind == kind));

    public static Check StagesNothing(Rubric dim, string label) =>
        new(dim, label, (o, _) => o.Staged.Count == 0);

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

    /// <summary>Rubric D: genuine ambiguity — asks which, stages nothing, runs no command.</summary>
    public static Check Clarifies(string label = "asks which (genuine ambiguity)") =>
        new(Rubric.D_ClarifyVsGuess, label, (o, _) =>
            o.Staged.Count == 0
            && !o.Tools.Any(t => LlmTools.IsStagedCommand(t.Name))
            && LooksLikeWhichQuestion(o.Final));

    public static Check FinalLacks(string pattern, string label, Rubric dim) =>
        new(dim, label, (o, _) => !Regex.IsMatch(o.Final, pattern, RegexOptions.IgnoreCase));

    public static Check FinalHas(string pattern, string label, Rubric dim) =>
        new(dim, label, (o, _) => Regex.IsMatch(o.Final, pattern, RegexOptions.IgnoreCase));

    /// <summary>Passes if any sub-check passes — for cases with more than one acceptable trajectory.</summary>
    public static Check AnyOf(Rubric dim, string label, params Check[] checks) =>
        new(dim, label, (o, fx) => checks.Any(c => c.Predicate(o, fx)));

    // --- matchers ------------------------------------------------------------------------------

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

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

    private static bool LooksLikeWhichQuestion(string final) =>
        final.Contains('?') &&
        Regex.IsMatch(final, @"\bwhich\b|which one|do you mean|could you (clarify|specify)|can you (clarify|specify)|please (clarify|specify)",
            RegexOptions.IgnoreCase);
}
