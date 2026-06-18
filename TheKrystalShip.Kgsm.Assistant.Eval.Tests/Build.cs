using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Eval.Tests;

/// <summary>Builders for synthetic turn observations + fixtures, so the checks can be unit-tested
/// without a live model or host.</summary>
internal static class Build
{
    public static ResolvedFixtures Fixtures(string game = "factorio", string instance = "factorio-test") =>
        new(
            new[] { new InstanceFact(instance, game + ".bp", Running: true) },
            new[] { "minecraft", "valheim" },
            UniqueGameWord: game,
            UniqueGameInstance: instance,
            RunningInstance: instance,
            StoppedInstance: null,
            AnyInstance: instance,
            NeverInstalledGame: "minecraft");

    public static RecordedToolCall Tool(TheKrystalShip.Llm.Models.Tool name, params (string k, string? v)[] args) =>
        new(name, args.ToDictionary(a => a.k, a => a.v), Result: "ok", DurationMs: 1);

    public static PendingConfirmation Staged(ConfirmationKind kind, string instance = "factorio-test") =>
        new(kind, Target: instance, InstanceName: instance);

    public static TurnObservation Obs(
        string final = "",
        RecordedToolCall[]? tools = null,
        PendingConfirmation[]? staged = null) =>
        new("prompt", tools ?? Array.Empty<RecordedToolCall>(), staged ?? Array.Empty<PendingConfirmation>(),
            Iterations: 1, TurnOutcome.Ok, final);
}
