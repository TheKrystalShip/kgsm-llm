using FluentAssertions;

using TheKrystalShip.Kgsm.Assistant.Ports;


namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// Reading "look it up online" off what somebody actually typed.
/// </summary>
/// <remarks>
/// The model was measured declining to pass the scope even when asked in plain English, so this is
/// what makes an explicit request actually reach the web. It reads an instruction, never a topic —
/// nothing here decides whether a question needs current information, which stays the model's call.
/// </remarks>
public class AskedForTheWebTests
{
    [Theory]
    [InlineData("check online for the latest Valheim update")]
    [InlineData("search online for the next Terraria patch")]
    [InlineData("can you look that up online?")]
    [InlineData("Look it up on the internet please")]
    [InlineData("search the web for factorio mods")]
    [InlineData("google it")]
    [InlineData("what's the newest version? check the internet")]
    [InlineData("go find it online.")]
    [InlineData("browse the web and tell me")]
    public void AnInstructionToLookOutsideIsRead(string prompt) =>
        AskedForTheWeb.In(prompt).Should().BeTrue();

    [Theory]
    [InlineData("is minecraft online?")]
    [InlineData("how many players are online right now")]
    [InlineData("bring the web server up")]
    [InlineData("which servers are online")]
    [InlineData("restart the internet gateway container")]
    public void TalkingAboutSomethingBeingOnlineIsNotAnInstruction(string prompt) =>
        AskedForTheWeb.In(prompt).Should().BeFalse(
            "a server being online is the opposite subject, and matching it would send ordinary "
            + "questions about this host out to the web");

    [Theory]
    [InlineData("what's installed?")]
    [InlineData("stop minecraft")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnOrdinaryRequestAsksForNothing(string? prompt) =>
        AskedForTheWeb.In(prompt).Should().BeFalse();

    [Fact]
    public void PunctuationDoesNotHideTheInstruction()
    {
        AskedForTheWeb.In("check online, please!").Should().BeTrue();
        AskedForTheWeb.In("CHECK ONLINE").Should().BeTrue();
    }

    [Fact]
    public void TheTurnsIntentOnlyEverWidens()
    {
        // There is no phrase a person uses to confine the assistant to documentation they cannot see,
        // so this can send a search to the web and can do nothing else — a false positive costs a web
        // answer to something the docs also covered, and never a refused or narrowed search.
        SearchIntent.From("check online for the patch notes").Should().Be(SearchScope.Web);
        SearchIntent.From("what's installed?").Should().BeNull();
    }

    [Fact]
    public void OutsideATurnNothingIsRequired()
    {
        SearchIntent.Required.Should().BeNull();
    }

    [Fact]
    public void TheScopeIsClearedWhenTheTurnEnds()
    {
        using (SearchIntent.BeginTurn(SearchScope.Web))
            SearchIntent.Required.Should().Be(SearchScope.Web);

        SearchIntent.Required.Should().BeNull("a turn's request must not leak into the next one");
    }
}
