using TheKrystalShip.Kgsm.Assistant.Service.Speech;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

/// <summary>
/// The cut points decide what a listener hears and when. Getting them wrong is not a crash — it is an
/// answer read out with the punctuation pronounced, or a code block recited line by line, or a
/// sentence that never arrives because nothing after it ever terminated.
/// </summary>
public class SpokenSentencesTests
{
    /// <summary>Feeds a whole reply through as one delta and collects everything sayable.</summary>
    private static List<string> Say(params string[] deltas)
    {
        var sentences = new SpokenSentences();
        var said = new List<string>();

        foreach (string delta in deltas)
            said.AddRange(sentences.Take(delta));

        string? last = sentences.Flush();
        if (last is not null) said.Add(last);

        return said;
    }

    [Fact]
    public void ASentenceIsSaidWhenItEnds()
    {
        var said = Say("Factorio is running with four players online. ", "Terraria is stopped.");

        Assert.Equal(2, said.Count);
        Assert.Equal("Factorio is running with four players online.", said[0]);
        Assert.Equal("Terraria is stopped.", said[1]);
    }

    [Fact]
    public void TokenFragmentsAssembleIntoOneSentence()
    {
        // What the stream actually delivers: a delta is a piece of a token, not a word.
        var said = Say("Fact", "orio", " is", " run", "ning right", " now.");

        Assert.Single(said);
        Assert.Equal("Factorio is running right now.", said[0]);
    }

    [Fact]
    public void AShortSentenceWaitsForTheNextOneRatherThanBeingClipped()
    {
        // "Yes." alone is a syllable and a whole round trip. It rides along with what follows.
        var said = Say("Yes. It has been running since Tuesday afternoon.");

        Assert.Single(said);
        Assert.Equal("Yes. It has been running since Tuesday afternoon.", said[0]);
    }

    [Fact]
    public void AShortAnswerIsStillSaidWhenNothingFollowsIt()
    {
        // The minimum exists to avoid a clipped fragment mid-answer, not to swallow a short reply.
        var said = Say("Yes, it is.");

        Assert.Single(said);
        Assert.Equal("Yes, it is.", said[0]);
    }

    [Fact]
    public void AnAnswerThatNeverTerminatesIsStillSaid()
    {
        var said = Say("The server is starting and should be up shortly");

        Assert.Single(said);
        Assert.Equal("The server is starting and should be up shortly", said[0]);
    }

    [Fact]
    public void NothingSayableProducesNothing() =>
        Assert.Empty(Say("   ", "\n", ""));

    [Fact]
    public void AFencedCodeBlockIsNotRead()
    {
        // ⚠ The trap this class exists for. Segmenting first and stripping second reads the contents of
        // a fence out one line at a time — thirty seconds of punctuation.
        var said = Say(
            "Here is the config you asked about.\n",
            "```ini\n",
            "name = factorio\n",
            "port = 34197\n",
            "restart = always\n",
            "```\n",
            "Change the port and restart it.");

        Assert.DoesNotContain(said, s => s.Contains("34197"));
        Assert.DoesNotContain(said, s => s.Contains("factorio", StringComparison.Ordinal) && s.Contains('='));
        Assert.Contains(said, s => s.Contains("Here is the config"));
        Assert.Contains(said, s => s.Contains("Change the port"));
    }

    [Fact]
    public void AFenceOpenedAndNeverClosedSwallowsTheRest()
    {
        // An answer that opened a fence and stopped IS a code block, whatever arrives next.
        var said = Say("Try this:\n", "```bash\n", "kgsm --start factorio\n", "kgsm --status factorio\n");

        Assert.DoesNotContain(said, s => s.Contains("kgsm"));
        Assert.Contains(said, s => s.Contains("Try this"));
    }

    [Fact]
    public void AFenceSplitAcrossDeltasIsStillRecognised()
    {
        // The fence marker arrives in pieces like everything else does.
        var said = Say("Run this.\n", "`", "``", "\n", "rm -rf /tmp/x\n", "``", "`\n", "That clears the cache.");

        Assert.DoesNotContain(said, s => s.Contains("rm -rf"));
        Assert.Contains(said, s => s.Contains("That clears the cache"));
    }

    [Fact]
    public void MarkupIsDroppedAndTheWordsAreKept()
    {
        var said = Say("The **factorio** server is `running` on port 34197 right now.");

        Assert.Single(said);
        Assert.Equal("The factorio server is running on port 34197 right now.", said[0]);
    }

    [Fact]
    public void AListItemIsItsOwnBreathWithoutItsBullet()
    {
        var said = Say("- factorio is running with four players\n", "- terraria has been stopped since Monday\n");

        Assert.Equal(2, said.Count);
        Assert.Equal("factorio is running with four players", said[0]);
        Assert.Equal("terraria has been stopped since Monday", said[1]);
    }

    [Fact]
    public void AHeadingLosesItsHashes()
    {
        var said = Say("## Server status\n", "Everything on this host is running normally today.");

        Assert.Contains("Server status", said[0]);
        Assert.DoesNotContain("#", said[0]);
    }

    [Fact]
    public void ALinkIsReadAsItsText()
    {
        var said = Say("The panel is at [the control panel](https://example.invalid) whenever you need it.");

        Assert.Single(said);
        Assert.DoesNotContain("[", said[0]);
        Assert.Contains("the control panel", said[0]);
    }

    [Theory]
    [InlineData("The model file is ggml-small.en.bin and it lives in the state directory.")]
    [InlineData("This host is running kgsm.sh version 1.2.3 with everything current.")]
    [InlineData("Reach the panel at panel.example.invalid whenever you need to look.")]
    public void ADotInsideAWordDoesNotEndTheSentence(string reply)
    {
        // ⚠ Version numbers, filenames and hostnames are full of dots, and this domain is full of all
        // three. Cutting at one splits a sentence mid-word and spends a synthesis request saying half
        // of it — so a sentence ends at punctuation FOLLOWED BY SPACE, decided one character late.
        var said = Say(reply);

        Assert.Single(said);
    }

    [Fact]
    public void TwoSentencesOnOneLineAreTwoOfThem()
    {
        var said = Say("The server is running normally now. It was restarted about an hour ago.");

        Assert.Equal(2, said.Count);
        Assert.Equal("The server is running normally now.", said[0]);
        Assert.Equal("It was restarted about an hour ago.", said[1]);
    }

    [Fact]
    public void SentencesComeOutInTheOrderTheyWereWritten()
    {
        var said = Say(
            "First the server was stopped by the scheduler. ",
            "Then it was started again a minute later. ",
            "It has been up ever since that restart.");

        Assert.Equal(3, said.Count);
        Assert.StartsWith("First", said[0]);
        Assert.StartsWith("Then", said[1]);
        Assert.StartsWith("It has been up", said[2]);
    }
}
