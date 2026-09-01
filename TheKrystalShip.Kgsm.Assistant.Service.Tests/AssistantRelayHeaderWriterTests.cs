using System.Net.Http;

using FluentAssertions;

using TheKrystalShip.Kgsm.Assistant.Relay;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

/// <summary>
/// The per-turn headers a leaf writes when it relays a turn.
/// </summary>
/// <remarks>
/// One writer for every leaf, because these decide which prompts a turn reads, which audit origin it is
/// recorded under and which conversation it continues. The property worth pinning is what the writer
/// does <em>not</em> put on the wire: who the turn is for and what they may do are named and resolved
/// elsewhere, so no header here can raise what somebody is allowed to do.
/// </remarks>
public class AssistantRelayHeaderWriterTests
{
    [Fact]
    public void The_calling_leaf_is_named()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/turn");

        new AssistantRelay(RelayLeaf.Bot).Write(request);

        Header(request, AssistantRelay.LeafHeader).Should().Be(RelayLeaf.Bot);
    }

    [Fact]
    public void No_identity_and_no_authority_are_written()
    {
        // The whole point of the scheme: a caller names the person on X-Kgsm-Acting and authenticates as
        // a member, and the assistant reads the tier from its own accounts. A header carrying either
        // here would be a second, softer answer to a question that has one.
        var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/turn");

        new AssistantRelay(RelayLeaf.Bot).Write(request, new RelayCall(AutoAct: true));

        request.Headers.Should().NotContain(h => h.Key.StartsWith("X-Relay-User"));
        request.Headers.Should().NotContain(h => h.Key == "X-Relay-Tier");
        request.Headers.Should().NotContain(h => h.Key == "X-Relay-Secret");
        request.Headers.Authorization.Should().BeNull();
    }

    [Fact]
    public void A_call_with_no_per_turn_parts_writes_only_the_leaf()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/turn");

        new AssistantRelay(RelayLeaf.Bot).Write(request);

        Header(request, AssistantRelay.AutoActHeader).Should().BeNull();
        Header(request, AssistantRelay.ConversationIdHeader).Should().BeNull();
        Header(request, AssistantRelay.RoomHeader).Should().BeNull();
    }

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public void The_auto_accept_preference_is_written_either_way(bool autoAct, string expected)
    {
        // Written even when false, because the assistant reads anything that is not exactly "true" as
        // propose-only — stating it is how a caller says it means propose-only rather than saying nothing.
        var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/turn");

        new AssistantRelay(RelayLeaf.Bot).Write(request, new RelayCall(AutoAct: autoAct));

        Header(request, AssistantRelay.AutoActHeader).Should().Be(expected);
    }

    [Fact]
    public void A_conversation_and_a_room_are_written_when_given()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/turn");

        new AssistantRelay(RelayLeaf.Bot).Write(
            request, new RelayCall(ConversationId: "chat-1", Room: "g1-t9"));

        Header(request, AssistantRelay.ConversationIdHeader).Should().Be("chat-1");
        Header(request, AssistantRelay.RoomHeader).Should().Be("g1-t9");
    }

    [Fact]
    public void A_blank_conversation_or_room_says_nothing()
    {
        // A writer and a reader disagreeing about what "blank" means is how an empty header comes to
        // mean something.
        var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/turn");

        new AssistantRelay(RelayLeaf.Bot).Write(request, new RelayCall(ConversationId: "  ", Room: ""));

        Header(request, AssistantRelay.ConversationIdHeader).Should().BeNull();
        Header(request, AssistantRelay.RoomHeader).Should().BeNull();
    }

    [Fact]
    public void A_control_character_cannot_split_a_header()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/turn");

        new AssistantRelay(RelayLeaf.Bot).Write(request, new RelayCall(Room: "g1\r\nX-Injected: yes"));

        Header(request, AssistantRelay.RoomHeader).Should().Be("g1X-Injected: yes");
        request.Headers.Should().NotContain(h => h.Key == "X-Injected");
    }

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;
}
