using FluentAssertions;

using TheKrystalShip.Kgsm.Assistant.Relay;
using TheKrystalShip.KGSM.Auth;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

/// <summary>
/// The shared relay header writer, pinned against the filter that reads it on the other side of the
/// wire. These two live in one repo precisely so a spelling cannot drift between them; the tests
/// assert the literal header names rather than the constants, so renaming a constant alone does not
/// quietly agree with itself.
/// </summary>
public sealed class AssistantRelayHeaderWriterTests
{
    private static readonly RelayPrincipal Caller = new("385730677141929985", "Heisen", KgsmTier.Operator);

    private static HttpRequestMessage Written(
        RelayPrincipal? caller = null, RelayCall? call = null,
        string? secret = "s3cret", string leaf = RelayLeaf.Bot)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/turn");
        new AssistantRelay(secret, leaf).Write(request, caller ?? Caller, call);
        return request;
    }

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    [Fact]
    public void WritesTheSecret_TheIdentity_TheTier_AndTheLeaf()
    {
        var request = Written();

        Header(request, "X-Relay-Secret").Should().Be("s3cret");
        Header(request, "X-Relay-User").Should().Be("385730677141929985");
        Header(request, "X-Relay-User-Name").Should().Be("Heisen");
        Header(request, "X-Relay-Tier").Should().Be(KgsmTiers.ToWire(KgsmTier.Operator));
        Header(request, "X-Relay-Leaf").Should().Be("kgsm-bot");
    }

    /// <summary>
    /// The per-call headers are omitted entirely when there is no call context, so a caller that
    /// forwards only an identity cannot accidentally assert an auto-accept decision it never made.
    /// </summary>
    [Fact]
    public void WithNoCallContext_TheresNoAutoAct_AndNoConversationScope()
    {
        var request = Written(call: null);

        Header(request, "X-Relay-Auto-Act").Should().BeNull();
        Header(request, "X-Relay-Conversation-Id").Should().BeNull();
    }

    [Fact]
    public void AutoAct_IsWrittenExplicitlyBothWays()
    {
        Header(Written(call: new RelayCall(AutoAct: true)), "X-Relay-Auto-Act").Should().Be("true");
        Header(Written(call: new RelayCall(AutoAct: false)), "X-Relay-Auto-Act").Should().Be("false");
    }

    [Fact]
    public void ConversationId_IsForwardedWhenPresent_AndOmittedWhenBlank()
    {
        Header(Written(call: new RelayCall(ConversationId: "chat-abc")), "X-Relay-Conversation-Id")
            .Should().Be("chat-abc");
        Header(Written(call: new RelayCall(ConversationId: "   ")), "X-Relay-Conversation-Id")
            .Should().BeNull();
    }

    /// <summary>
    /// A display name is user-controlled and crosses a trust boundary. A CR/LF reaching a header is
    /// how a request gets split, so control characters are dropped before they are ever written.
    /// </summary>
    [Fact]
    public void AControlCharacterInADisplayName_CannotSplitAHeader()
    {
        var request = Written(new RelayPrincipal("42", "Heisen\r\nX-Relay-Tier: admin", KgsmTier.Viewer));

        Header(request, "X-Relay-User-Name").Should().Be("HeisenX-Relay-Tier: admin");
        Header(request, "X-Relay-Tier").Should().Be(KgsmTiers.ToWire(KgsmTier.Viewer));
    }

    /// <summary>
    /// No secret means the relay path is not usable. Writing an empty one would present an
    /// unauthenticated call as a relay attempt; omitting it lets the assistant fall through to its
    /// session path and answer 401, which is the honest outcome.
    /// </summary>
    [Fact]
    public void WithNoSecret_NoSecretHeaderIsWritten_AndTheRelayReportsItselfUnconfigured()
    {
        var relay = new AssistantRelay(secret: "", leaf: RelayLeaf.Api);
        relay.IsConfigured.Should().BeFalse();

        var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        relay.Write(request, Caller);
        Header(request, "X-Relay-Secret").Should().BeNull();
    }

    /// <summary>
    /// The leaf a consumer declares is the leaf the assistant maps to an origin. Pinning the two
    /// constants against their literals is what keeps a rename from silently agreeing with itself
    /// on both ends while meaning something new.
    /// </summary>
    [Fact]
    public void TheLeafConstants_AreTheirWireSpellings()
    {
        RelayLeaf.Bot.Should().Be("kgsm-bot");
        RelayLeaf.Api.Should().Be("kgsm-api");
    }

    /// <summary>
    /// The room rides the same writer as everything else, so a leaf cannot spell it differently from
    /// the filter that reads it. Whether it is HONOURED is the receiver's decision — this side only
    /// has to state it correctly.
    /// </summary>
    [Fact]
    public void WritesTheRoom_WhenTheCallNamesOne() =>
        Header(Written(call: new RelayCall(Room: "g1-t9")), "X-Relay-Room").Should().Be("g1-t9");

    [Fact]
    public void WithNoRoom_TheHeaderIsAbsent()
    {
        Header(Written(call: new RelayCall(ConversationId: "chat-1")), "X-Relay-Room").Should().BeNull();
        Header(Written(call: new RelayCall()), "X-Relay-Room").Should().BeNull();
    }

    /// <summary>A blank room says nothing, so it is not said at all.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankRoom_IsNotWritten(string room) =>
        Header(Written(call: new RelayCall(Room: room)), "X-Relay-Room").Should().BeNull();
}
