using FluentAssertions;

using Microsoft.Extensions.Options;

namespace TheKrystalShip.Kgsm.Assistant.Eval.Tests;

/// <summary>
/// Guards the eval-semantics contract behind the Phase 4 rename: the E-group rubrics
/// (<c>CalledTool(search)</c> / <c>DidNotCallTool(search)</c>) only mean anything if <c>search</c>
/// is OFFERED during a run. The harness forces it on (no real provider is wired in the eval), and
/// this asserts that link by EXECUTION — so the rubrics can't silently revert to unsatisfiable
/// (CalledTool) or vacuous (DidNotCallTool). No model or live kgsm needed; the harness only wires DI.
/// </summary>
public class HarnessSearchAvailabilityTests
{
    [Fact]
    public void The_harness_offers_the_search_tool_so_the_E_group_rubrics_are_meaningful()
    {
        EvalOptions.TryParse(["--kgsm", "/bin/true"], out var options, out var error)
            .Should().BeTrue(because: error);

        var harness = Harness.Build(options);

        harness.Resolve<IOptions<SearchOptions>>().Value.Available
            .Should().BeTrue("the eval scores routing, so `search` must be offered even with no real source wired");
    }
}
