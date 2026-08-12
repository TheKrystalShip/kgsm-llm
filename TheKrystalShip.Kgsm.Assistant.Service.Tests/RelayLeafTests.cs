using FluentAssertions;

using TheKrystalShip.Kgsm.Assistant;
using TheKrystalShip.Kgsm.Assistant.Infrastructure;
using TheKrystalShip.Kgsm.Assistant.Service.Security;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

/// <summary>
/// <c>X-Relay-Leaf</c> and what is derived from it: the prompt overrides a surface reads and the
/// audit origin its actions record under. Both fail closed to the assistant's own, so a relay that
/// does not speak the header is unaffected by its existence.
/// </summary>
public sealed class RelayLeafTests
{
    [Theory]
    [InlineData("kgsm-bot")]
    [InlineData("kgsm-api")]
    [InlineData("a")]
    [InlineData("leaf9")]
    public void AWellFormedLeafName_IsAccepted(string raw) =>
        LeafName.Validate(raw).Should().Be(raw);

    /// <summary>
    /// Rejected, never repaired. A sanitizer that strips illegal characters would turn
    /// <c>kgsm/bot</c> into a lookup against a directory named <c>kgsmbot</c> — a silent misread,
    /// where refusing falls through to the assistant's own text.
    /// </summary>
    [Theory]
    [InlineData("../etc")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("kgsm/bot")]
    [InlineData("kgsm\\bot")]
    [InlineData("kgsm.bot")]
    [InlineData("KGSM-BOT")]
    [InlineData("kgsm bot")]
    [InlineData("-kgsm")]
    [InlineData("-")]
    [InlineData("")]
    [InlineData(null)]
    public void AMalformedLeafName_IsRefused_NotCleanedUp(string? raw) =>
        LeafName.Validate(raw).Should().BeNull();

    [Fact]
    public void ALeafNameLongerThanTheCap_IsRefused() =>
        LeafName.Validate(new string('a', LeafName.MaxLength + 1)).Should().BeNull();

    // ------------------------------------------------------------------- origins ----------------

    /// <summary>
    /// The bot's surface is Discord, so its actions are recorded as Discord's — otherwise a Discord
    /// chat action is indistinguishable in the engine's journal from a browser chat action, while the
    /// slash command beside it still says <c>discord</c>.
    /// </summary>
    [Fact]
    public void TheBotsActions_AreRecordedAsDiscords() =>
        RelayLeaves.OriginFor(RelayLeaves.Bot).Should().Be("discord");

    /// <summary>
    /// The api relays a browser chat, so the origin is the assistant — the surface the person was
    /// using, not the leaf that carried the call. This is why the mapping is a table and not a copy
    /// of the leaf name.
    /// </summary>
    [Fact]
    public void TheApisRelayedChat_IsStillTheAssistants() =>
        RelayLeaves.OriginFor(RelayLeaves.Api).Should().Be(Invocation.AssistantOrigin);

    [Theory]
    [InlineData(null)]
    [InlineData("kgsm-something-new")]
    [InlineData("discord")]
    public void AnAbsentOrUnknownLeaf_RecordsTheAssistantsOrigin_NeverTheCallersClaim(string? leaf) =>
        RelayLeaves.OriginFor(leaf).Should().Be(Invocation.AssistantOrigin);

    // ------------------------------------------------------------------- rooms ------------------

    /// <summary>
    /// A room is the one conversation key with no verified user id in it, so who may open one is a
    /// grant rather than a default. The bot holds it because a Discord thread is a real place with a
    /// membership and a lifetime; nothing else does.
    /// </summary>
    [Fact]
    public void OnlyTheBot_MayOpenARoom()
    {
        RelayLeaves.OpensRooms(RelayLeaves.Bot).Should().BeTrue();
        RelayLeaves.OpensRooms(RelayLeaves.Api).Should().BeFalse();
    }

    /// <summary>
    /// Fail-closed, like every other relay header: a leaf this service does not recognise — or none at
    /// all — is not granted a shared conversation by omission.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("kgsm-monitor")]
    [InlineData("KGSM-BOT")]
    public void AnUnlistedLeaf_MayNotOpenARoom(string? leaf) =>
        RelayLeaves.OpensRooms(leaf).Should().BeFalse();
}
