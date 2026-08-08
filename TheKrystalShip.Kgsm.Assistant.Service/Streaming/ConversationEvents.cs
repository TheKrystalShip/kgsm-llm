using System.Collections.Concurrent;
using System.Threading.Channels;

namespace TheKrystalShip.Kgsm.Assistant.Service.Streaming;

/// <summary>The event names <c>GET /events</c> frames. Both the SSE <c>event:</c> line and the in-band
/// <c>type</c> discriminator carry the same constant, exactly as the turn stream does.</summary>
internal static class ConversationStream
{
    /// <summary>The opening frame, naming this stream so a client can recognise its own echoes.</summary>
    public const string Hello = "hello";

    /// <summary>A conversation's switches now stand somewhere; the payload says where.</summary>
    public const string Switches = "conversation.switches";

    /// <summary>A conversation was started and is now listable.</summary>
    public const string Started = "conversation.started";

    /// <summary>A conversation was soft-deleted and should leave the list.</summary>
    public const string Deleted = "conversation.deleted";

    /// <summary>
    /// A conversation's log grew — a turn, or a compaction checkpoint. The payload names the
    /// conversation and nothing more: what changed is the transcript, which a client re-reads rather
    /// than has mirrored to it frame by frame.
    /// </summary>
    public const string Activity = "conversation.activity";
}

/// <summary>The opening frame of a stream: the id this connection answers to.</summary>
internal sealed record StreamHello(string StreamId);

/// <summary>
/// Something happened to a conversation. <paramref name="Origin"/> is the stream id of the connection
/// whose caller caused it, when one named itself — a surface skips its own, having already applied the
/// change it asked for. Null when nothing named itself (the relay path, or a client not tracking one),
/// which every stream then applies.
/// </summary>
internal sealed record ConversationChanged(string ConversationId, string? Origin);

/// <summary>
/// Where a conversation's switches now stand, EFFECTIVE — resolved against the host's configured
/// default exactly as the listing resolves them, so a surface applying this frame lands on the same
/// value it would read back.
/// </summary>
internal sealed record SwitchesChanged(string ConversationId, string? Origin, bool Think, bool Autorun);

/// <summary>One frame to fan out: the event name and the payload it carries.</summary>
internal sealed record ConversationEvent(string Name, object Payload);

/// <summary>A live subscription. Disposing it detaches the stream from the bus.</summary>
internal sealed class ConversationEventSubscription(
    string streamId, ChannelReader<ConversationEvent> reader, Action release) : IDisposable
{
    public string StreamId { get; } = streamId;
    public ChannelReader<ConversationEvent> Reader { get; } = reader;
    public void Dispose() => release();
}

/// <summary>
/// Fan-out of conversation changes to a user's own open streams, so two surfaces looking at one
/// conversation agree without either polling the other.
/// </summary>
internal interface IConversationEventBus
{
    /// <summary>
    /// Deliver <paramref name="ev"/> to every live stream belonging to <paramref name="userId"/>, and to
    /// no one else's. Never blocks and never throws: a mutation is not held up by whether anyone is
    /// listening.
    /// </summary>
    void Publish(string userId, ConversationEvent ev);

    /// <summary>Attach a new stream for <paramref name="userId"/>. Dispose to detach.</summary>
    ConversationEventSubscription Subscribe(string userId);
}

/// <inheritdoc/>
internal sealed class ConversationEventBus : IConversationEventBus
{
    /// <summary>
    /// Per-stream backlog. Deep enough that no realistic burst reaches it, and bounded because a stream
    /// nobody is draining must cost a fixed amount rather than grow. On overflow the OLDEST frame is
    /// dropped: every frame states where something now stands rather than how it moved, so the newest is
    /// the true one — and a client re-reads the listing when its stream reconnects, which is what closes
    /// any gap a drop leaves.
    /// </summary>
    private const int Backlog = 64;

    // One flat map, keyed by stream. Publishing walks it and filters by user: a nested per-user map
    // would need its own emptiness race handled, and at the scale a leaf serves (a guild's worth of
    // people, a surface or two each) walking is not worth a second data structure.
    private readonly ConcurrentDictionary<string, Subscriber> _streams = new(StringComparer.Ordinal);

    private sealed record Subscriber(string UserId, Channel<ConversationEvent> Channel);

    public void Publish(string userId, ConversationEvent ev)
    {
        foreach (var (_, subscriber) in _streams)
        {
            if (string.Equals(subscriber.UserId, userId, StringComparison.Ordinal))
                subscriber.Channel.Writer.TryWrite(ev);
        }
    }

    public ConversationEventSubscription Subscribe(string userId)
    {
        var channel = Channel.CreateBounded<ConversationEvent>(new BoundedChannelOptions(Backlog)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        var streamId = Guid.NewGuid().ToString("N");
        _streams[streamId] = new Subscriber(userId, channel);

        return new ConversationEventSubscription(streamId, channel.Reader, () =>
        {
            _streams.TryRemove(streamId, out _);
            channel.Writer.TryComplete();
        });
    }
}
