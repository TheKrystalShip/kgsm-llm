using TheKrystalShip.Kgsm.Assistant.Blueprints;
using TheKrystalShip.Kgsm.Assistant.Envelope;

namespace TheKrystalShip.Kgsm.Assistant.Ports;

/// <summary>
/// The <c>create_blueprint</c> capability (<c>assistant-blueprint-authoring-plan.md</c>): given a game
/// missing from the catalog, researches it, drafts and test-installs a native-Linux blueprint, verifies
/// it empirically (boots + listens), and keeps it only if verified — the whole pipeline behind ONE
/// model-facing tool. Implementations MUST NOT throw; a run that cannot proceed (disabled, infeasible,
/// unverified) is a normal <see cref="BlueprintAuthoringOutcome"/>, never an exception. The concrete
/// pipeline needs kgsm-lib's write-side blueprint/instance authorities, so — unlike most assistant
/// ports — its implementation lives in Infrastructure (<c>BlueprintAuthoringAggregator</c>), not here;
/// this port is what keeps the dispatcher decoupled from that dependency.
/// </summary>
public interface IBlueprintAuthoring
{
    Task<ToolResult<BlueprintAuthoringData>> AuthorAsync(string game, CancellationToken cancellationToken = default);
}

/// <summary>Default <see cref="IBlueprintAuthoring"/> for hosts that haven't enabled blueprint
/// authoring: every call reports it honestly as not configured, so embedding the assistant library
/// never breaks DI just because <c>BlueprintAuthoring:Enabled</c> is unset (false by default
/// everywhere). A host that wants the real pipeline calls <c>AddKgsmAdapters</c> with it enabled, which
/// registers the concrete aggregator that wins over this default.</summary>
internal sealed class DisabledBlueprintAuthoring : IBlueprintAuthoring
{
    public Task<ToolResult<BlueprintAuthoringData>> AuthorAsync(string game, CancellationToken cancellationToken = default)
    {
        game = game.Trim();
        var summary = $"Automatic blueprint authoring isn't enabled on this host, so I can't research " +
                       $"and build a config for \"{game}\" myself.";
        var data = new BlueprintAuthoringData(
            BlueprintAuthoringOutcome.Disabled, game, null, [], null, OfferInstance: false);
        return Task.FromResult(new ToolResult<BlueprintAuthoringData>(
            LlmTools.CreateBlueprint, Confidence.Confirmed, new ResultRef(ResourceKind.Blueprint, game), summary, data));
    }
}
