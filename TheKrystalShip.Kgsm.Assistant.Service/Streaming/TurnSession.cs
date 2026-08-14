using System.Text;
using System.Threading.Channels;

using TheKrystalShip.Kgsm.Assistant.Service.PendingConfirmations;
using TheKrystalShip.Kgsm.Assistant.Service.Security;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Service.Streaming;

/// <summary>Where a turn stands. A queued turn has not been offered to the model yet.</summary>
internal enum TurnState
{
    Queued,
    Running,
    Done,
}

/// <summary>One frame to write: the SSE event name, and the payload that goes in its `data`.</summary>
internal sealed record TurnFrame(string Name, object Payload);

/// <summary>
/// One tool call as the attach snapshot states it. Mirrors the pair of live frames
/// (<c>tool.start</c> then <c>tool.result</c>) so a surface that attached late renders the call
/// through the same component as one it watched happen.
/// </summary>
public sealed record TurnToolState(
    string Id,
    string Name,
    IReadOnlyDictionary<string, string?> Arguments,
    string State,
    string? Summary,
    object? Card);

/// <summary>A queued turn as the snapshot names it: enough to show it and to cancel it.</summary>
public sealed record QueuedTurn(string TurnId, string Prompt);

/// <summary>
/// `turn.attach` — everything a consumer needs to render a turn it did not watch from the start.
/// It is both the greeting a late attach gets and the redraw a consumer that fell behind gets, so
/// there is one way to arrive at a correct view rather than a join path and a repair path.
/// </summary>
public sealed record TurnAttachEvent(
    string TurnId,
    string ConversationId,
    string Prompt,
    string State,
    string Text,
    string? Thinking,
    IReadOnlyList<TurnToolState> Tools,
    IReadOnlyList<CommandProposedEvent> Proposals,
    IReadOnlyList<QueuedTurn> Queued,
    DoneEvent? Done,
    StreamErrorEvent? Error);

/// <summary>
/// A consumer attached to a turn: a bounded queue of frames and a flag saying it fell behind.
/// <para>
/// Falling behind is not corruption here. The writer sees <see cref="NeedsRedraw"/>, throws away what
/// it has queued and re-attaches from the session's own state — the same move a terminal makes when a
/// pane's contents are no longer trustworthy. A consumer is never handed deltas with a hole in them.
/// </para>
/// </summary>
internal sealed class TurnConsumer
{
    /// <summary>Deep enough that only a genuinely stalled reader reaches it, and bounded because a
    /// reader that has stopped draining must cost a fixed amount rather than grow.</summary>
    private const int Backlog = 512;

    private readonly Channel<TurnFrame> _frames = Channel.CreateBounded<TurnFrame>(
        new BoundedChannelOptions(Backlog) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    public ChannelReader<TurnFrame> Reader => _frames.Reader;

    /// <summary>Set when a frame could not be queued; cleared by the writer once it has redrawn.</summary>
    public bool NeedsRedraw { get; private set; }

    internal void Offer(TurnFrame frame)
    {
        if (!_frames.Writer.TryWrite(frame))
            NeedsRedraw = true;
    }

    /// <summary>Throw away the backlog and clear the flag — called under the session's lock, so the
    /// snapshot taken beside it cannot miss a frame written in between.</summary>
    internal void Redrawn()
    {
        while (_frames.Reader.TryRead(out _)) { }
        NeedsRedraw = false;
    }

    internal void Complete() => _frames.Writer.TryComplete();
}

/// <summary>
/// One turn, running at the leaf with its own lifetime, broadcasting to every surface attached to it.
/// <para>
/// The session — not the request that asked for it — owns the run. That is what lets the turn survive
/// the surface that started it, lets a second surface watch it, and lets any of them stop it. The
/// frames every consumer receives are produced <b>once</b>, here, so two surfaces cannot be shown
/// different renderings of the same turn.
/// </para>
/// </summary>
internal sealed class TurnSession
{
    private readonly Lock _gate = new();
    private readonly List<TurnConsumer> _consumers = [];

    private readonly StringBuilder _text = new();
    private readonly StringBuilder _thinking = new();
    private readonly List<TurnToolState> _tools = [];
    private readonly List<CommandProposedEvent> _proposals = [];
    // The staged operations behind those frames, kept so a BUFFERED caller can be handed the same
    // shape it always was. A streamed proposal and a buffered one describe one staging, and deriving
    // the second from the first rather than staging twice is what keeps them from disagreeing.
    private readonly List<(PendingConfirmation Confirmation, string Token)> _staged = [];
    private DoneEvent? _done;
    private StreamErrorEvent? _error;

    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _finished =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TurnSession(
        string turnId, AuthPrincipal principal, string conversationId, string chatId, TurnRun run)
    {
        TurnId = turnId;
        Principal = principal;
        ConversationId = conversationId;
        ChatId = chatId;
        Run = run;
    }

    public string TurnId { get; }
    public AuthPrincipal Principal { get; }

    /// <summary>The full stored key (<c>web:{user}:{chat}</c>) the turn runs against.</summary>
    public string ConversationId { get; }

    /// <summary>The client-facing chat id, which is what every frame names.</summary>
    public string ChatId { get; }

    /// <summary>Everything needed to run this turn when its place in the queue comes up.</summary>
    public TurnRun Run { get; }

    public TurnState State { get; private set; } = TurnState.Queued;

    /// <summary>Whether a stop has been asked for. A turn that never started simply never starts.</summary>
    public bool Stopping { get; private set; }

    /// <summary>Completes when the turn is done, however it ended. Awaited by the buffered caller.</summary>
    public Task Finished => _finished.Task;

    public CancellationToken Token => _cts.Token;

    public int ConsumerCount { get { lock (_gate) { return _consumers.Count; } } }

    /// <summary>The reply as it stands, which is what a stopped turn is recorded with.</summary>
    public string TextSoFar { get { lock (_gate) { return _text.ToString(); } } }

    /// <summary>What this turn staged, for a buffered caller to be handed.</summary>
    public IReadOnlyList<(PendingConfirmation Confirmation, string Token)> Staged
    {
        get { lock (_gate) { return [.. _staged]; } }
    }

    /// <summary>The token accounting the turn reported, or null when it reported none.</summary>
    public UsageDto? Usage { get { lock (_gate) { return _done?.Usage; } } }

    /// <summary>The in-band failure this turn ended with, if it failed.</summary>
    public string? Error { get { lock (_gate) { return _error?.Message; } } }

    internal void RecordStaged(PendingConfirmation confirmation, string token)
    {
        lock (_gate) { _staged.Add((confirmation, token)); }
    }

    public void MarkRunning()
    {
        lock (_gate) { State = TurnState.Running; }
    }

    /// <summary>
    /// Stop this turn. A running one has its generation cancelled; a queued one is simply marked so it
    /// is skipped when its turn comes. Idempotent — the second caller is not an error, because two
    /// surfaces pressing the same button is the ordinary case, not a race to police.
    /// </summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (Stopping || State == TurnState.Done)
                return;
            Stopping = true;
        }
        _cts.Cancel();
    }

    internal void Finish()
    {
        lock (_gate)
        {
            State = TurnState.Done;
            foreach (var consumer in _consumers)
                consumer.Complete();
        }
        _finished.TrySetResult();
        _cts.Dispose();
    }

    /// <summary>
    /// Record a frame into the session's own state and hand it to every consumer. Both halves happen
    /// under one lock, so a consumer attaching concurrently either sees the frame in its snapshot or
    /// receives it after — never neither.
    /// </summary>
    internal void Broadcast(TurnFrame frame, Action<TurnFrame> alsoPublish)
    {
        lock (_gate)
        {
            Accumulate(frame);
            foreach (var consumer in _consumers)
                consumer.Offer(frame);
        }
        alsoPublish(frame);
    }

    private void Accumulate(TurnFrame frame)
    {
        switch (frame.Name)
        {
            case TurnStream.TextDelta:
                _text.Append(((TokenEvent)frame.Payload).Text);
                break;
            case TurnStream.ThinkingDelta:
                _thinking.Append(((ThinkingEvent)frame.Payload).Text);
                break;
            case TurnStream.ToolStart:
            {
                var start = (ToolStartEvent)frame.Payload;
                _tools.Add(new TurnToolState(start.Id, start.Tool, start.Arguments, "running", null, null));
                break;
            }
            case TurnStream.ToolResult:
            {
                var result = (ToolResultEvent)frame.Payload;
                var at = _tools.FindLastIndex(t => t.Id == result.Id);
                var done = new TurnToolState(
                    result.Id, result.Tool,
                    at >= 0 ? _tools[at].Arguments : new Dictionary<string, string?>(),
                    "done", result.Summary, result.Result);
                if (at >= 0) _tools[at] = done; else _tools.Add(done);
                break;
            }
            case TurnStream.CommandProposed:
                _proposals.Add((CommandProposedEvent)frame.Payload);
                break;
            case TurnStream.Done:
                _done = (DoneEvent)frame.Payload;
                break;
            case TurnStream.Error:
                _error = (StreamErrorEvent)frame.Payload;
                break;
        }
    }

    /// <summary>
    /// Attach a consumer and hand back the state it must render before the frames start arriving.
    /// Taken under the lock so nothing can slip between the snapshot and the subscription.
    /// </summary>
    internal (TurnConsumer Consumer, TurnAttachEvent Attach) Attach(IReadOnlyList<QueuedTurn> queued)
    {
        lock (_gate)
        {
            var consumer = new TurnConsumer();
            _consumers.Add(consumer);
            if (State == TurnState.Done)
                consumer.Complete();
            return (consumer, SnapshotLocked(queued));
        }
    }

    internal void Detach(TurnConsumer consumer)
    {
        lock (_gate) { _consumers.Remove(consumer); }
    }

    /// <summary>The redraw a consumer that fell behind is given: drop its backlog and restate everything.</summary>
    internal TurnAttachEvent Redraw(TurnConsumer consumer, IReadOnlyList<QueuedTurn> queued)
    {
        lock (_gate)
        {
            consumer.Redrawn();
            return SnapshotLocked(queued);
        }
    }

    internal TurnAttachEvent Snapshot(IReadOnlyList<QueuedTurn> queued)
    {
        lock (_gate) { return SnapshotLocked(queued); }
    }

    private TurnAttachEvent SnapshotLocked(IReadOnlyList<QueuedTurn> queued) => new(
        TurnId,
        ChatId,
        Run.Prompt,
        State switch
        {
            TurnState.Queued => "queued",
            TurnState.Running => Stopping ? "stopping" : "running",
            _ => "done",
        },
        _text.ToString(),
        _thinking.Length == 0 ? null : _thinking.ToString(),
        [.. _tools],
        [.. _proposals],
        queued,
        _done,
        _error);
}

/// <summary>
/// Everything a queued turn needs when its place comes up. Authority is deliberately <b>not</b> here:
/// tier and auto-run are re-derived at the moment the turn runs, so a role taken away while it waited
/// takes effect on it. A snapshot of permissions carried forward is how a queue launders privilege
/// past a revocation.
/// </summary>
/// <remarks>
/// <c>Shared</c> says the turn belongs to a conversation several people hold together (a room), which
/// attributes both this prompt and the replayed history to their speakers. Carried rather than
/// re-read from the conversation key: which surface a key belongs to is the composing handler's to
/// know, and a prefix test here would be a second, quietly divergent copy of that scheme.
/// <para>
/// <c>Style</c> is carried for the opposite reason authority is not: it is what the surface that asked
/// needs the answer to look like, which does not change while the turn waits. Presentation, never
/// permission — a queued turn is no shorter or longer a licence for having been styled.
/// </para>
/// </remarks>
internal sealed record TurnRun(
    string Prompt,
    IReadOnlyList<string>? Tools,
    string? DraftYaml,
    string? RelayLeaf,
    KgsmTierSource Authority,
    bool Shared = false,
    ReplyStyle Style = ReplyStyle.Default);

/// <summary>
/// How this turn's authority is established when it runs. The relay carries the caller's verified tier
/// with it (a relay host may have no Discord config of its own); a direct session bearer re-derives its
/// own from Discord. Either way the question is asked at execution, never at enqueue.
/// </summary>
internal sealed record KgsmTierSource(bool FromRelay, KGSM.Auth.KgsmTier RelayTier, bool RelayAutoAct);
