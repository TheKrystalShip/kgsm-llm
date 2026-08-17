using FluentAssertions;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// <see cref="FabricatedFigureClaim"/>: a figure in the reply that appears in nothing the turn was
/// given. Most of these are FALSE-POSITIVE cases — the check earns its place by staying silent on
/// every legitimate number a reply contains, since one that argues with correct answers gets removed.
/// </summary>
public sealed class FabricatedFigureClaimTests
{
    /// <summary>The shape a real status read hands the model.</summary>
    private const string ToolOutput = """
        Ketchup (palworld) — running.
        - Configured ports: 8211/udp, 27015/udp
        - The host firewall has both open.
        - Disk use: 12.4 GB across 3 backups.
        """;

    // ── the failure it exists for ────────────────────────────────────────────

    [Fact]
    public void AMisquotedPort_IsCaught()
    {
        // The measured defect: the tool said 27015 and the reply says a plausible neighbour.
        var unbacked = FabricatedFigureClaim.UnbackedIn(
            "Ketchup is using ports 8211/udp and 21075/udp.", ToolOutput);

        unbacked.Should().Equal("21075");
    }

    [Fact]
    public void EveryUnbackedFigure_IsNamed_Once()
    {
        var unbacked = FabricatedFigureClaim.UnbackedIn(
            "It uses 21075 and 17015, and 21075 again.", ToolOutput);

        // Named so the nudge can quote them; de-duplicated so it does not repeat itself.
        unbacked.Should().Equal("21075", "17015");
    }

    [Fact]
    public void ATruncatedPort_IsNotMistakenForTheRealOne()
    {
        // 2701 is a substring of 27015. Matching on substrings alone would call this backed and let a
        // truncated port through as measured.
        FabricatedFigureClaim.UnbackedIn("The port is 2701.", ToolOutput).Should().Equal("2701");
    }

    // ── what it must stay silent about ───────────────────────────────────────

    [Fact]
    public void AQuotedFigure_IsBacked()
    {
        FabricatedFigureClaim.UnbackedIn(
            "Ketchup is using ports 8211/udp and 27015/udp.", ToolOutput).Should().BeEmpty();
    }

    [Fact]
    public void SmallNumbers_AreNeverChecked()
    {
        // Counts, percentages and ordinals are computed rather than copied, and arguing with the
        // model's arithmetic is not what this is for. "3 of 8" appears nowhere in the tool output.
        FabricatedFigureClaim.UnbackedIn(
            "3 of your 8 servers are running, and 2 have updates. CPU is at 47%.", ToolOutput)
            .Should().BeEmpty();
    }

    [Fact]
    public void AFigureWithThousandsSeparators_IsBacked()
    {
        FabricatedFigureClaim.UnbackedIn("It is on port 27,015.", ToolOutput).Should().BeEmpty();
    }

    [Fact]
    public void ADecimal_IsReadAsTwoFigures_NotJoinedIntoOne()
    {
        // 12.4 must not be flattened to 124 — joining them would make a fabricated 124 look backed.
        FabricatedFigureClaim.UnbackedIn("Backups take 12.4 GB.", ToolOutput).Should().BeEmpty();
    }

    [Fact]
    public void AFigureFromTheRequest_IsBacked()
    {
        // The person supplied it; repeating it back is not a fabricated measurement.
        FabricatedFigureClaim.UnbackedIn(
            "You asked about port 29999 — that is not one of its ports.",
            "what about port 29999?\n" + ToolOutput)
            .Should().BeEmpty();
    }

    [Fact]
    public void AFigureFromTheInjectedPromptLists_IsBacked()
    {
        // The system prompt is part of what the turn was given — the clock's year lands there.
        FabricatedFigureClaim.UnbackedIn(
            "As of 2026 it is on 8211.", "Right now it is 17:05 on 2026-08-17.\n" + ToolOutput)
            .Should().BeEmpty();
    }

    [Fact]
    public void AVersionString_IsCheckedPerSegment_AndBackedWhenQuoted()
    {
        FabricatedFigureClaim.UnbackedIn(
            "It is running 1.4.5.6.", "The server reports version 1.4.5.6.").Should().BeEmpty();
    }

    [Fact]
    public void NothingGiven_FlagsNothing()
    {
        // Fail open. An empty ledger means the turn recorded nothing, not that every figure is invented.
        FabricatedFigureClaim.UnbackedIn("It is on port 27015.", "").Should().BeEmpty();
        FabricatedFigureClaim.UnbackedIn("It is on port 27015.", null).Should().BeEmpty();
    }

    [Fact]
    public void AnEmptyReply_FlagsNothing()
    {
        FabricatedFigureClaim.UnbackedIn("", ToolOutput).Should().BeEmpty();
        FabricatedFigureClaim.UnbackedIn(null, ToolOutput).Should().BeEmpty();
    }

    // ── the wording the model and the reader get ─────────────────────────────

    [Fact]
    public void TheNudge_QuotesTheFiguresBack()
    {
        // A nudge that only says "a number was wrong" leaves the model to pick which, and it picks the
        // one it is most confident about — the one it invented.
        var nudge = FabricatedFigureClaim.NudgeFor(["21075"]);

        nudge.Should().Contain("21075");
        nudge.Should().Contain("digit for digit");
    }

    [Fact]
    public void TheNudge_ReadsNaturally_ForOneFigureAndForSeveral()
    {
        FabricatedFigureClaim.NudgeFor(["21075"]).Should().Contain("the figure 21075");
        FabricatedFigureClaim.NudgeFor(["21075", "17015"]).Should().Contain("the figures 21075, 17015");
    }

    [Fact]
    public void TheCorrection_IsDetectedSoItIsNeverAppendedTwice()
    {
        FabricatedFigureClaim.CorrectionIsPresentIn("answer" + FabricatedFigureClaim.Correction)
            .Should().BeTrue();
        FabricatedFigureClaim.CorrectionIsPresentIn("answer").Should().BeFalse();
    }

    [Fact]
    public void TheCorrection_ClaimsNoCorrectValue()
    {
        // None is known. Saying which figure is right would be inventing a second one.
        FabricatedFigureClaim.Correction.Should().Contain("not from any tool result");
        FabricatedFigureClaim.Correction.Should().NotMatchRegex(@"\d{4,}");
    }
}
