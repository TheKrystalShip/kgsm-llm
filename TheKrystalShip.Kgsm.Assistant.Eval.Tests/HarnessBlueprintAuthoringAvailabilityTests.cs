using FluentAssertions;

using Microsoft.Extensions.Options;

namespace TheKrystalShip.Kgsm.Assistant.Eval.Tests;

/// <summary>
/// Guards the eval-semantics contract for the B-group rubrics (<c>CalledTool(create_blueprint)</c> /
/// <c>DidNotCallTool(create_blueprint)</c>), mirroring <see cref="HarnessFetchAvailabilityTests"/>: they
/// only mean anything if <c>create_blueprint</c> is OFFERED during a run. The harness forces it on (no
/// real <c>BlueprintAuthoring:Enabled</c> is set in the eval), so a model-routed call exercises real DI
/// wiring but hits the real aggregator's own <c>Enabled</c> gate first — still false — which reports
/// itself as not configured without ever touching kgsm-lib's write-side authorities. No model or live
/// kgsm needed; the harness only wires DI.
/// </summary>
public class HarnessBlueprintAuthoringAvailabilityTests
{
    [Fact]
    public void The_harness_offers_the_create_blueprint_tool_so_the_B_group_rubrics_are_meaningful()
    {
        EvalOptions.TryParse(["--kgsm", "/bin/true"], out var options, out var error)
            .Should().BeTrue(because: error);

        var harness = Harness.Build(options);

        harness.Resolve<IOptions<BlueprintAuthoringFlags>>().Value.Available
            .Should().BeTrue("the eval scores routing, so `create_blueprint` must be offered even with BlueprintAuthoring:Enabled unset");
    }
}
