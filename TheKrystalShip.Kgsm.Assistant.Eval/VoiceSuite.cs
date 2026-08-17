using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Eval;

/// <summary>
/// One question a person would ask out loud, and what the answer must still do however short it gets.
/// </summary>
/// <param name="Id">Stable id, so a rerun's rows line up with an earlier one's.</param>
/// <param name="Prompt">The question, with the usual <see cref="ResolvedFixtures.Fill"/> placeholders.</param>
/// <param name="RequiredRoles">Fixture roles the case needs; an unfillable one skips it, loudly.</param>
/// <param name="Authorized">Whether the asking user may act — the staging cases need it.</param>
/// <param name="MustCallAnyOf">
/// Tools of which at least one has to be called. This is the routing floor: an answer that got shorter
/// by no longer checking is not a shorter answer, it is a guess.
/// </param>
/// <param name="MustStage">Whether the turn has to stage a confirmation.</param>
internal sealed record VoiceCase(
    string Id,
    string Prompt,
    IReadOnlyList<FixtureRole> RequiredRoles,
    bool Authorized,
    IReadOnlyList<Capability> MustCallAnyOf,
    bool MustStage = false);

/// <summary>
/// The spoken-reply corpus: a handful of questions asked the way somebody asks them in a voice channel,
/// each run once in <see cref="ReplyStyle.Default"/> and once in <see cref="ReplyStyle.Voice"/> so the
/// two are measured against the same host, the same model and the same fixtures.
/// </summary>
/// <remarks>
/// <para>
/// What it measures is REPLY LENGTH, in characters, which is the thing the voice surface is paid in:
/// speech runs at a fixed rate, so a reply's length is its duration and there is nothing a listener can
/// skim. It reports characters and sentence counts — both counted, neither converted into a seconds
/// figure this process did not measure.
/// </para>
/// <para>
/// Length alone would be a trap: an empty reply is the shortest possible one. So each case carries the
/// same kind of trajectory floor the routing benchmark uses — a tool that must be called, and for the
/// mutating ones a confirmation that must be staged and a reply that must still say so. A row is only
/// interesting if its floor is green; a shorter reply that lost its tool call is a regression that
/// happens to look like a win.
/// </para>
/// <para>
/// Non-destructive by construction, exactly as the routing harness is (its CLAUDE.md invariant #2):
/// this only ever calls <see cref="IServerAssistant.RunAsync"/>, which STAGES. Nothing here confirms,
/// and there must never be a path from here to execution.
/// </para>
/// </remarks>
internal static class VoiceSuite
{
    /// <summary>Bump when a case's prompt or floor changes, so old result files compare honestly.</summary>
    public const string Version = "voice-1";

    public static readonly IReadOnlyList<VoiceCase> Cases = new[]
    {
        // The canonical one: a yes/no question whose honest answer is two words, and which a written
        // assistant answers in a paragraph.
        new VoiceCase(
            "V1", "is {unique_game} running?",
            new[] { FixtureRole.UniqueGame }, Authorized: false,
            new[] { LlmTools.ServerInfo, LlmTools.RunHealthCheck }),

        // A single measured number. The failure to watch for is the reply that recites every other
        // metric it happened to fetch on the way. get_performance is the tool that carries a memory
        // figure, and is what the model reaches for; the others are here because a health read carries
        // one too, and the floor asks that SOMETHING was measured — not which route was taken.
        new VoiceCase(
            "V2", "how much memory is {unique_game} using?",
            new[] { FixtureRole.UniqueGame }, Authorized: false,
            new[] { LlmTools.GetPerformance, LlmTools.RunHealthCheck, LlmTools.ServerInfo }),

        // A mutating request: it must still stage, and the reply must still say a confirmation is
        // waiting. Short must not become "okay" over a pending action.
        new VoiceCase(
            "V3", "stop the {stopped_game} server",
            new[] { FixtureRole.Stopped }, Authorized: true,
            new[] { LlmTools.ServerCommand }, MustStage: true),

        // Answerable from the injected lists with no tool at all, so it isolates prose length from
        // routing entirely — hence the empty floor.
        new VoiceCase(
            "V4", "what servers do we have?",
            new[] { FixtureRole.AnyInstance }, Authorized: false,
            Array.Empty<Capability>()),

        // A number a listener has to catch first time, and the case where markdown and stray
        // punctuation hurt most.
        new VoiceCase(
            "V5", "what port is {unique_game} on?",
            new[] { FixtureRole.UniqueGame }, Authorized: false,
            new[] { LlmTools.ServerInfo, LlmTools.GetNetwork }),

        // An open-ended one. The written answer is genuinely a report; the spoken one has to be a
        // verdict, and it must not become a cheerful verdict on a server it never checked.
        new VoiceCase(
            "V6", "is {unique_game} healthy?",
            new[] { FixtureRole.UniqueGame }, Authorized: false,
            new[] { LlmTools.RunHealthCheck, LlmTools.ServerInfo }),
    };
}
