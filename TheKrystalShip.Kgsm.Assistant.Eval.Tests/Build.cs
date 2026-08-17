using TheKrystalShip.Kgsm.Assistant;
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

    /// <summary>
    /// A recorded call, named by the capability it implements. A synthetic observation carries no
    /// catalog, and <see cref="TurnObservation.Matches"/> falls back to the capability id for exactly
    /// that case — so these tests exercise the checks without needing to know what any tool is called.
    /// </summary>
    public static RecordedToolCall Tool(Capability capability, params (string k, string? v)[] args) =>
        new(new TheKrystalShip.Llm.Models.Tool(capability.Id),
            args.ToDictionary(a => a.k, a => a.v), Summary: "ok", DurationMs: 1);

    public static PendingConfirmation Staged(ConfirmationKind kind, string instance = "factorio-test") =>
        new(kind, Target: instance, InstanceName: instance);

    public static TurnObservation Obs(
        string final = "",
        RecordedToolCall[]? tools = null,
        PendingConfirmation[]? staged = null,
        int iterations = 1,
        TurnOutcome outcome = TurnOutcome.Ok,
        Func<string, string, string?>? fileSnapshot = null) =>
        new("prompt", tools ?? Array.Empty<RecordedToolCall>(), staged ?? Array.Empty<PendingConfirmation>(),
            iterations, outcome, final)
        {
            FileSnapshot = fileSnapshot,
        };

    /// <summary>A staged write, as the dispatcher builds one: the path on ConfigKey, the resolved new
    /// content on ConfigValue.</summary>
    public static PendingConfirmation StagedWrite(string path, string content, string instance = "factorio-test") =>
        new(ConfirmationKind.WriteFile, Target: instance, InstanceName: null, ConfigKey: path, ConfigValue: content);

    /// <summary>A host whose files are the given (path → content) pairs, for scoring a staged payload
    /// against the file it claims to be an edit of. Any other path reads as unreadable.</summary>
    public static Func<string, string, string?> Host(params (string path, string content)[] files) =>
        (_, path) => files.FirstOrDefault(f => f.path == path) is { path: not null } hit ? hit.content : null;
}
