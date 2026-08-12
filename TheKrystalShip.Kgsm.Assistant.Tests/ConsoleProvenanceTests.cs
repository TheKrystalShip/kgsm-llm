using FluentAssertions;

using TheKrystalShip.Kgsm.Assistant.Consoles;
using TheKrystalShip.Kgsm.Assistant.Ports;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// The line above a console read that says which run it is.
/// <para>
/// The failure it exists to stop: a server crashes, the supervisor restarts it, and the console read
/// afterwards is a clean boot. Those lines are indistinguishable from a healthy server's, and the
/// model sees only this text — so a read handed over without provenance produces "no errors in the
/// logs", which is true of the run it was shown and false of the question it was asked.
/// </para>
/// </summary>
public class ConsoleProvenanceTests
{
    private const string Instance = "romestead";
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 11, 0, 0, TimeSpan.Zero);

    private static ConsoleRunInfo Current(int index = 0) => new(index, Current: true, EndedAt: null);

    private static ConsoleRunInfo Ended(int index, DateTimeOffset endedAt, string outcome, int? exit = null) =>
        new(index, Current: false, endedAt, outcome, exit);

    private static string Describe(IReadOnlyList<ConsoleRunInfo> runs, int index = 0) =>
        ConsoleProvenance.Describe(Instance, runs, index, Now);

    [Fact]
    public void ARecentCrashRestartPointsAtTheRunThatHoldsTheCrash()
    {
        // The whole case in one assertion. The current run began 14 minutes ago; the crash is in the
        // run behind it, and the reader is told so and told how to reach it.
        var runs = new[]
        {
            Current(),
            Ended(1, Now.AddMinutes(-14), ConsoleRunInfo.CrashedOutcome, exit: 137),
        };

        var text = Describe(runs);

        text.Should().Contain("run 0, the run in progress");
        text.Should().Contain("restarted 14 minutes ago");
        text.Should().Contain("ended in a crash").And.Contain("exit 137");
        text.Should().Contain("run=1");
    }

    [Fact]
    public void ALongRunningServerIsNotToldAboutAnOldRestart()
    {
        // Past the window the server's own output IS the subject, and pointing at a run from days ago
        // is noise that would push the reader off the question it was actually asked.
        var runs = new[]
        {
            Current(),
            Ended(1, Now.AddDays(-3), ConsoleRunInfo.CrashedOutcome, exit: 139),
        };

        var text = Describe(runs);

        text.Should().NotContain("⚠").And.NotContain("run=1");
        // The boundary is still stated: these lines start somewhere, and where is a fact.
        text.Should().Contain("picks up where the previous run left off").And.Contain("3 days ago");
    }

    [Fact]
    public void AnUnrecordedEndingIsSaidToBeUnrecorded_NotClean()
    {
        // Every run rotated before the supervisor kept a ledger reports "unknown". Describing that as
        // an ordinary ending would launder an absence of knowledge into a fact.
        var runs = new[] { Current(), Ended(1, Now.AddMinutes(-5), ConsoleRunInfo.UnknownOutcome) };

        var text = Describe(runs);

        text.Should().Contain("how it ended was never recorded")
            .And.Contain("not the same as it having ended cleanly");
    }

    [Fact]
    public void ADeliberateStopIsNamedAsOne()
    {
        // Worth saying plainly: it tells the reader the restart was intended and there is no crash to
        // go looking for — which the old output could not distinguish from any other restart.
        var runs = new[] { Current(), Ended(1, Now.AddMinutes(-5), "stopped") };

        var text = Describe(runs);

        text.Should().Contain("was stopped deliberately").And.NotContain("crash");
    }

    [Fact]
    public void TheOnlyRunOnRecordSaysSo()
    {
        var text = Describe(new[] { Current() });

        text.Should().Contain("the only run on record");
        text.Should().NotContain("previous run");
    }

    [Fact]
    public void AStoppedServersLastRunIsDescribedAsEnded()
    {
        // Run 0 is not always in progress: a stopped server's last output sits at the live path until
        // the next spawn rotates it. Calling that "the run in progress" would claim it is running.
        var runs = new[] { Ended(0, Now.AddMinutes(-30), ConsoleRunInfo.CrashedOutcome, exit: 1) };

        var text = Describe(runs);

        text.Should().Contain("run 0, which ended in a crash").And.Contain("Nothing is running");
        text.Should().NotContain("in progress");
    }

    [Fact]
    public void ReadingAnOlderRunByNumberGetsNoPointerBackToTheRuns()
    {
        // A caller naming a run has already found its way there; repeating the directions is noise.
        var runs = new[]
        {
            Current(),
            Ended(1, Now.AddMinutes(-10), ConsoleRunInfo.CrashedOutcome, exit: 137),
            Ended(2, Now.AddMinutes(-40), "stopped"),
        };

        var text = Describe(runs, index: 1);

        text.Should().Contain("run 1, which ended in a crash");
        text.Should().NotContain("⚠").And.NotContain("run=1");
    }

    [Fact]
    public void AnUnplaceableReadIsStillReturned_WithNoClaimAboutWhichRunItIs()
    {
        // The supervisor served the lines but not the run list. The output is real and still worth
        // showing; asserting a position for it would be the invention this type exists to prevent.
        var text = Describe([]);

        text.Should().Be("Recent console output for romestead:");
    }

    [Fact]
    public void GivingUpIsDescribedAsACrashTheSupervisorStoppedRetrying()
    {
        var runs = new[] { Current(), Ended(1, Now.AddMinutes(-2), ConsoleRunInfo.GaveUpOutcome, exit: 1) };

        var text = Describe(runs);

        text.Should().Contain("ended in a crash").And.Contain("stopped retrying");
    }
}
