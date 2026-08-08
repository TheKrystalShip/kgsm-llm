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

    /// <summary>The verdict standing on one turn; the payload names the turn and what it now says.</summary>
    public const string Feedback = "conversation.feedback";

    /// <summary>
    /// A conversation's log grew — a turn, or a compaction checkpoint. The payload names the
    /// conversation and nothing more: what changed is the transcript, which a client re-reads rather
    /// than has mirrored to it frame by frame.
    /// </summary>
    public const string Activity = "conversation.activity";

    /// <summary>
    /// The whole state of a turn in progress: what a surface must render before the live frames start
    /// making sense. Sent when a surface attaches to a conversation, and again as a redraw when one
    /// falls too far behind to be given deltas.
    /// </summary>
    public const string TurnAttach = "turn.attach";

    /// <summary>What is running on a conversation and what is waiting behind it, restated whole.</summary>
    public const string TurnQueue = "turn.queue";
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

/// <summary>
/// The verdict that now stands on one turn. <paramref name="Rating"/> is <c>null</c> when a verdict was
/// withdrawn — the same shape the transcript reads back, so a surface applying this frame and one
/// re-reading the history land on the same bubble.
/// </summary>
internal sealed record FeedbackChanged(
    string ConversationId, string? Origin, long TurnId, string? Rating, string? Note);

/// <summary>One frame to fan out: the event name and the payload it carries.</summary>
internal sealed record ConversationEvent(string Name, object Payload);

/// <summary>A live subscription. Disposing it detaches the stream from the bus.</summary>
internal sealed class ConversationEventSubscription(
    string streamId, ChannelReader<ConversationEvent> reader, Subscriber subscriber, Action release)
    : IDisposable
{
    public string StreamId { get; } = streamId;
    public ChannelReader<ConversationEvent> Reader { get; } = reader;

    /// <summary>The conversation this stream is looking at, or null when it is looking at none.</summary>
    public string? Attached => subscriber.Attached;

    /// <summary>Whether frames were dropped for this stream and it owes itself a redraw.</summary>
    public bool NeedsRedraw => subscriber.NeedsRedraw;

    /// <summary>Drop the backlog and clear the flag, once the redraw has been written.</summary>
    public void Redrawn() => subscriber.Redrawn();

    public void Dispose() => release();
}

/// <summary>
/// One open stream. <see cref="Attached"/> is the conversation it is looking at: turn frames are
/// high-rate and belong only to the surfaces rendering them, where the state events belong to every
/// stream because they are about the chat LIST rather than about one conversation.
/// </summary>
internal sealed class Subscriber(string userId, Channel<ConversationEvent> channel)
{
    public string UserId { get; } = userId;
    public Channel<ConversationEvent> Channel { get; } = channel;
    public volatile string? Attached;

    /// <summary>Set when a frame could not be queued. A turn frame missed is a hole in a reply, so the
    /// writer answers it with a fresh attach rather than carrying on.</summary>
    public bool NeedsRedraw { get; private set; }

    public void Offer(ConversationEvent ev)
    {
        if (Channel.Writer.TryWrite(ev))
            return;
        // The state events state where something now stands rather than how it moved, so losing one is
        // survivable; a turn frame is a delta and losing one is not. Either way the answer is the same:
        // stop trusting the backlog and redraw.
        NeedsRedraw = true;
    }

    public void Redrawn()
    {
        while (Channel.Reader.TryRead(out _)) { }
        NeedsRedraw = false;
    }
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

    /// <summary>
    /// Deliver to that person's streams that are looking at <paramref name="conversationId"/>, and to
    /// no others. This is the turn-frame path: those frames arrive at token rate and mean nothing to a
    /// surface rendering a different conversation, so they are not sent to one.
    /// </summary>
    void PublishToAttached(string userId, string conversationId, ConversationEvent ev);

    /// <summary>
    /// Deliver to exactly one of that person's streams. Used to answer an attach on the stream itself,
    /// so a surface learns what it attached to through the same channel everything else arrives on —
    /// one path into its renderer rather than an HTTP response and a frame that could disagree.
    /// </summary>
    bool PublishTo(string streamId, string userId, ConversationEvent ev);

    /// <summary>Attach a new stream for <paramref name="userId"/>. Dispose to detach.</summary>
    ConversationEventSubscription Subscribe(string userId);

    /// <summary>Point a stream at a conversation, so it starts receiving that turn's frames.</summary>
    bool Attach(string streamId, string userId, string? conversationId);

    /// <summary>
    /// Whether this person is around: a stream open now, or one that closed within
    /// <paramref name="grace"/>. It is what decides whether a turn nobody is attached to keeps running
    /// — a phone locking its screen must not kill the reply a desktop is watching, and a wifi handover
    /// must not either.
    /// </summary>
    bool PresentWithin(string userId, TimeSpan grace);
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

    // When each person's last stream closed, so a turn survives the gap between a surface going away
    // and the same person's next one arriving.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSeen = new(StringComparer.Ordinal);

    public void Publish(string userId, ConversationEvent ev)
    {
        foreach (var (_, subscriber) in _streams)
        {
            if (string.Equals(subscriber.UserId, userId, StringComparison.Ordinal))
                subscriber.Offer(ev);
        }
    }

    public void PublishToAttached(string userId, string conversationId, ConversationEvent ev)
    {
        foreach (var (_, subscriber) in _streams)
        {
            if (string.Equals(subscriber.UserId, userId, StringComparison.Ordinal)
                && string.Equals(subscriber.Attached, conversationId, StringComparison.Ordinal))
                subscriber.Offer(ev);
        }
    }

    public bool PublishTo(string streamId, string userId, ConversationEvent ev)
    {
        if (!_streams.TryGetValue(streamId, out var subscriber)
            || !string.Equals(subscriber.UserId, userId, StringComparison.Ordinal))
            return false;
        subscriber.Offer(ev);
        return true;
    }

    public ConversationEventSubscription Subscribe(string userId)
    {
        // A turn frame per token, so the backlog is deep enough that only a stalled reader reaches it.
        // DropWrite rather than DropOldest: losing the OLDEST delta silently is how a reply arrives with
        // a hole in the middle, where a refused write is something the writer can answer with a redraw.
        var channel = Channel.CreateBounded<ConversationEvent>(new BoundedChannelOptions(Backlog)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

        var streamId = Guid.NewGuid().ToString("N");
        var subscriber = new Subscriber(userId, channel);
        _streams[streamId] = subscriber;

        return new ConversationEventSubscription(streamId, channel.Reader, subscriber, () =>
        {
            _streams.TryRemove(streamId, out _);
            _lastSeen[userId] = DateTimeOffset.UtcNow;
            channel.Writer.TryComplete();
        });
    }

    public bool Attach(string streamId, string userId, string? conversationId)
    {
        // Scoped to the caller: a stream id is not a handle on somebody else's stream.
        if (!_streams.TryGetValue(streamId, out var subscriber)
            || !string.Equals(subscriber.UserId, userId, StringComparison.Ordinal))
            return false;
        subscriber.Attached = conversationId;
        return true;
    }

    public bool PresentWithin(string userId, TimeSpan grace)
    {
        foreach (var (_, subscriber) in _streams)
        {
            if (string.Equals(subscriber.UserId, userId, StringComparison.Ordinal))
                return true;
        }
        return _lastSeen.TryGetValue(userId, out var last)
            && DateTimeOffset.UtcNow - last <= grace;
    }
}
