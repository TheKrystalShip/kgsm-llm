using FluentAssertions;

using Microsoft.Extensions.Options;

namespace TheKrystalShip.Kgsm.Assistant.Eval.Tests;

/// <summary>
/// Guards the eval-semantics contract for the F-group rubrics (<c>CalledTool(fetch_url)</c> /
/// <c>DidNotCallTool(fetch_url)</c>), mirroring <see cref="HarnessSearchAvailabilityTests"/>: they only
/// mean anything if <c>fetch_url</c> is OFFERED during a run. The harness forces it on (no real
/// <c>WebFetch:Enabled</c> is set in the eval), and this asserts that link by EXECUTION so the rubrics
/// can't silently revert to unsatisfiable (CalledTool) or vacuous (DidNotCallTool). No model or live
/// kgsm needed; the harness only wires DI.
/// </summary>
public class HarnessFetchAvailabilityTests
{
    [Fact]
    public void The_harness_offers_the_fetch_url_tool_so_the_F_group_rubrics_are_meaningful()
    {
        EvalOptions.TryParse(["--kgsm", "/bin/true"], out var options, out var error)
            .Should().BeTrue(because: error);

        var harness = Harness.Build(options);

        harness.Resolve<IOptions<FetchOptions>>().Value.Available
            .Should().BeTrue("the eval scores routing, so `fetch_url` must be offered even with WebFetch:Enabled unset");
    }
}
