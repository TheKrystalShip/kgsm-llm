using System.Collections.Concurrent;

using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Infrastructure;
using TheKrystalShip.Kgsm.Assistant.Service.Configuration;
using TheKrystalShip.Kgsm.Assistant.Service.PendingConfirmations;
using TheKrystalShip.Kgsm.Assistant.Service.Security;
using TheKrystalShip.Kgsm.Assistant.Service.Speech;
using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.Llm.Conversation;
using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Service.Streaming;

/// <summary>What asking for a turn produced.</summary>
internal enum TurnAdmission
{
    /// <summary>Nothing was running, so it started.</summary>
    Running,

    /// <summary>Something was running, so it waits its turn.</summary>
    Queued,

    /// <summary>Too many are already waiting; nothing was accepted.</summary>
    QueueFull,
}

internal sealed record TurnAdmissionResult(TurnAdmission Outcome, TurnSession? Session);

/// <summary>
/// The running turns, one per conversation, and what is waiting behind each.
/// <para>
/// A conversation runs <b>one turn at a time</b>: the model is answering into a single history, and two
/// turns against it would each read a context the other has not written yet. A second prompt therefore
/// waits rather than racing — up to <see cref="QueueDepth"/> of them, after which the answer is that
/// there are too many, not a longer queue nobody remembers authoring.
/// </para>
/// </summary>
internal interface ITurnRegistry
{
    /// <summary>Start the turn, or queue it behind what is running, or refuse it.</summary>
    TurnAdmissionResult Admit(AuthPrincipal principal, string conversationId, string chatId, TurnRun run);

    /// <summary>The turn running on a conversation, if any.</summary>
    TurnSession? Running(string conversationId);

    /// <summary>What is waiting on a conversation, in the order it will run.</summary>
    IReadOnlyList<QueuedTurn> Queued(string conversationId);

    /// <summary>
    /// Stop a running turn or cancel a queued one. Scoped to <paramref name="userId"/>, so a turn id is
    /// no handle on somebody else's conversation. Idempotent: <c>true</c> means it was found and is
    /// ending, and asking twice is not an error.
    /// </summary>
    bool Cancel(string turnId, string userId);

    /// <summary>
    /// Stop any running turn whose person is no longer around. A turn survives the surface that started
    /// it, but not its person leaving: the box's single GPU is reserved away from the game servers, so a
    /// reply nobody will read must not go on being generated.
    /// </summary>
    void SweepAbandoned(TimeSpan grace);
}

/// <inheritdoc/>
internal sealed class TurnRegistry : ITurnRegistry
{
    /// <summary>How many turns may wait behind the running one. Enough to type a couple of follow-ups
    /// ahead; short enough that a queue never becomes a backlog with GPU committed to it.</summary>
    public const int QueueDepth = 3;

    private readonly ConcurrentDictionary<string, ConversationTurns> _byConversation = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TurnSession> _byTurnId = new(StringComparer.Ordinal);

    private readonly IServiceScopeFactory _scopes;
    private readonly IServerAssistant _assistant;
    private readonly IPendingConfirmationStore _pending;
    private readonly IConversationEventBus _bus;
    private readonly IInvocationContext _invocation;
    private readonly IOptions<AssistantServiceOptions> _assistantOptions;
    private readonly IOptions<ConversationOptions> _conversationOptions;
    private readonly ISpokenAudio _audio;
    private readonly ILogger<TurnRegistry> _log;

    public TurnRegistry(
        IServiceScopeFactory scopes,
        IServerAssistant assistant,
        IPendingConfirmationStore pending,
        IConversationEventBus bus,
        IInvocationContext invocation,
        IOptions<AssistantServiceOptions> assistantOptions,
        IOptions<ConversationOptions> conversationOptions,
        ISpokenAudio audio,
        ILogger<TurnRegistry> log)
    {
        _scopes = scopes;
        _assistant = assistant;
        _pending = pending;
        _bus = bus;
        _invocation = invocation;
        _assistantOptions = assistantOptions;
        _conversationOptions = conversationOptions;
        _audio = audio;
        _log = log;
    }

    /// <summary>One conversation's running turn and the turns waiting behind it.</summary>
    private sealed class ConversationTurns
    {
        public readonly Lock Gate = new();
        public TurnSession? Running;
        public readonly List<TurnSession> Queue = [];
    }

    public TurnAdmissionResult Admit(
        AuthPrincipal principal, string conversationId, string chatId, TurnRun run)
    {
        var turns = _byConversation.GetOrAdd(conversationId, _ => new ConversationTurns());
        TurnSession session;
        bool startNow;

        lock (turns.Gate)
        {
            if (turns.Running is not null && turns.Queue.Count >= QueueDepth)
                return new TurnAdmissionResult(TurnAdmission.QueueFull, null);

            session = new TurnSession(
                "t_" + Guid.NewGuid().ToString("N"), principal, conversationId, chatId, run);
            _byTurnId[session.TurnId] = session;

            startNow = turns.Running is null;
            if (startNow) turns.Running = session;
            else turns.Queue.Add(session);
        }

        if (startNow)
        {
            Launch(turns, session);
            return new TurnAdmissionResult(TurnAdmission.Running, session);
        }

        // The queue is part of what every attached surface can see, so a prompt never waits invisibly.
        PublishQueue(session);
        return new TurnAdmissionResult(TurnAdmission.Queued, session);
    }

    public TurnSession? Running(string conversationId) =>
        _byConversation.TryGetValue(conversationId, out var turns) ? turns.Running : null;

    public IReadOnlyList<QueuedTurn> Queued(string conversationId)
    {
        if (!_byConversation.TryGetValue(conversationId, out var turns))
            return [];
        lock (turns.Gate)
            return [.. turns.Queue.Select(s => new QueuedTurn(s.TurnId, s.Run.Prompt))];
    }

    public bool Cancel(string turnId, string userId)
    {
        if (!_byTurnId.TryGetValue(turnId, out var session))
            return false;
        // A turn id names one person's turn. Anyone else asking is told there is no such turn rather
        // than that there is one they may not touch.
        if (!string.Equals(session.Principal.UserId, userId, StringComparison.Ordinal))
            return false;

        // Stopping a running turn cancels its generation; a queued one is marked and skipped when its
        // place comes up. Either way what is behind it in the queue is untouched — a stop means "this
        // one", never "everything I asked for".
        session.Stop();
        if (session.State == TurnState.Queued)
            Retire(session, ranToCompletion: false);
        return true;
    }

    public void SweepAbandoned(TimeSpan grace)
    {
        foreach (var (_, session) in _byTurnId)
        {
            if (session.State != TurnState.Running || session.Stopping)
                continue;
            // Attached right now counts, and so does a person whose surfaces have only just gone —
            // a screen locking or a network changing hands is not somebody leaving.
            if (session.ConsumerCount > 0 || _bus.PresentWithin(session.Principal.UserId, grace))
                continue;

            _log.LogInformation(
                "Turn {TurnId} has nobody watching and nobody present — stopping it", session.TurnId);
            session.Stop();
        }
    }

    /// <summary>Take a turn off the queue (it was cancelled before it ever ran) and say so.</summary>
    private void Retire(TurnSession session, bool ranToCompletion)
    {
        if (_byConversation.TryGetValue(session.ConversationId, out var turns))
        {
            lock (turns.Gate) { turns.Queue.Remove(session); }
        }
        _byTurnId.TryRemove(session.TurnId, out _);
        if (!ranToCompletion)
        {
            session.Finish();
            PublishQueue(session);
        }
    }

    private void Launch(ConversationTurns turns, TurnSession session)
    {
        session.MarkRunning();

        // A turn announces itself to everything watching, so they learn the prompt and start a bubble to
        // render into. Without it a watcher's first sight of a turn would be a text delta with nothing
        // to attach it to, and no way to tell one turn's frames from the next one's.
        //
        // Its own consumers get it too, not just the attached streams: a caller that posted while
        // something else was answering has been holding a queued session, and this is what tells it the
        // wait is over and its turn is the one running now.
        Publish(session, new TurnFrame(
            ConversationStream.TurnAttach, session.Snapshot(Queued(session.ConversationId))));

        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(session);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Turn {TurnId} failed outside its own error handling", session.TurnId);
            }
            finally
            {
                session.Finish();
                _byTurnId.TryRemove(session.TurnId, out _);

                // Before the next turn rather than after this one's reply, which are the same moment
                // from here: the answer has been delivered and its consumers are done, so nothing a
                // person is waiting on is delayed — and a turn queued behind this one inherits the
                // smaller context instead of racing the checkpoint being written under it.
                await CompactIfFullAsync(session);

                StartNext(turns, session);
            }
        });
    }

    /// <summary>
    /// Folds the conversation into a checkpoint when the turn that just ran left the context window
    /// close to full.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nobody is going to ask for this.</b> A conversation that is never compacted grows until the
    /// backend drops the front of it, and the first anybody knows is the assistant having forgotten
    /// something they never told it to forget. A room shared by a channel makes that certain: it is one
    /// conversation for the life of the place, and the people talking into it have no reason to know a
    /// context window exists. <c>/compact</c> remains for asking early.
    /// </para>
    /// <para>
    /// <b>Measured, never estimated.</b> The trigger is the occupancy the backend reported for the turn
    /// that just ran. A turn that reported none is left alone rather than compacted on a guess.
    /// </para>
    /// <para>
    /// <b>Failure is silent by design.</b> Compaction needs a model call, and one that fails costs a
    /// larger context next turn — not an answer. Raising it here would report a fault against a turn
    /// that has already succeeded and whose reply is on the person's screen.
    /// </para>
    /// </remarks>
    private async Task CompactIfFullAsync(TurnSession session)
    {
        var at = _conversationOptions.Value.CompactAtPercent;
        if (at <= 0) return;

        var usage = session.Usage;
        if (usage is null || usage.ContextWindow <= 0) return;
        if (usage.UsedTokens * 100 < usage.ContextWindow * at) return;

        try
        {
            using var scope = _scopes.CreateScope();
            var compactor = scope.ServiceProvider.GetRequiredService<IConversationCompactor>();

            var result = await compactor.CompactAsync(session.ConversationId, CancellationToken.None);
            if (result.IsFailure)
            {
                _log.LogWarning(
                    "Could not compact {Conversation} at {Used}/{Window} tokens: {Reason}",
                    session.ConversationId, usage.UsedTokens, usage.ContextWindow, result.Error);
                return;
            }

            if (result.Value!.Compacted)
                _log.LogInformation(
                    "Compacted {Conversation} — it was using {Used} of {Window} tokens",
                    session.ConversationId, usage.UsedTokens, usage.ContextWindow);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not compact {Conversation}", session.ConversationId);
        }
    }

    /// <summary>Hand the conversation to whatever was waiting, skipping anything cancelled meanwhile.</summary>
    private void StartNext(ConversationTurns turns, TurnSession finished)
    {
        TurnSession? next = null;
        lock (turns.Gate)
        {
            if (ReferenceEquals(turns.Running, finished))
                turns.Running = null;

            while (turns.Queue.Count > 0)
            {
                var candidate = turns.Queue[0];
                turns.Queue.RemoveAt(0);
                if (candidate.Stopping)
                {
                    _byTurnId.TryRemove(candidate.TurnId, out _);
                    candidate.Finish();
                    continue;
                }
                next = candidate;
                turns.Running = candidate;
                break;
            }
        }

        PublishQueue(finished);
        if (next is not null)
            Launch(turns, next);
    }

    /// <summary>
    /// Run one turn: re-derive its authority, then translate the agent's events into wire frames once
    /// and broadcast them. Producing the frames here rather than per-consumer is what makes two
    /// surfaces watching one turn see the same thing by construction.
    /// </summary>
    private async Task RunAsync(TurnSession session)
    {
        var run = session.Run;
        var principal = session.Principal;

        // Authority at EXECUTION time, never at enqueue: a role removed while this turn waited takes
        // effect on it. The relay carries the caller's verified tier because a relay host may hold no
        // Discord config of its own; a direct bearer re-derives its own from Discord.
        bool canPerform;
        bool autoExecute;
        var wantsAutoRun = false;
        using (var scope = _scopes.CreateScope())
        {
            var conversations = scope.ServiceProvider.GetRequiredService<IConversationStore>();
            wantsAutoRun = conversations.GetPreferences(session.ConversationId).Autorun ?? false;

            if (run.Authority.FromRelay)
            {
                canPerform = run.Authority.RelayTier >= KgsmTier.Operator && _assistantOptions.Value.ActionsEnabled;
                autoExecute = canPerform && wantsAutoRun && run.Authority.RelayAutoAct;
            }
            else
            {
                var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
                canPerform = await auth.CanPerformActionsAsync(principal, session.Token);
                autoExecute = canPerform && wantsAutoRun && await auth.IsAdminAsync(principal, session.Token);
            }
        }

        // The turn's think switch, read the same way — the conversation owns it.
        var think = ThinkFor(session);

        // Attribute anything this turn mutates to the asking person. Established INSIDE the run rather
        // than inherited from the request that asked for it: the request is long gone by the time a
        // queued turn executes, and an ambient value captured from it would attribute this turn to
        // whatever came before.
        using var provenance = _invocation.Begin(
            Invocation.ForAssistant(principal.DisplayName, RelayLeaves.OriginFor(run.RelayLeaf)));

        var proposalSeq = 0;
        var ttl = Math.Max(_assistantOptions.Value.Confirmation.TtlSeconds, 1);

        // The turn's last frame, held back while a spoken turn finishes saying itself.
        TurnFrame? ending = null;

        // Read aloud while it is written, when the surface that asked wants that and this host has an
        // engine to do it with. It is fed the same text deltas the reader is receiving and emits its
        // own frames beside them; nothing about the turn waits for it.
        await using SpokenTurn? spoken = run.Speak && _audio.Available
            ? new SpokenTurn(
                _audio,
                (seq, mime, audio) => Publish(session, new TurnFrame(
                    TurnStream.AudioDelta, new AudioEvent(seq, mime, Convert.ToBase64String(audio)))),
                _log,
                session.Token)
            : null;

        try
        {
            await foreach (var ev in _assistant.RunStreamAsync(
                               session.ConversationId, run.Prompt, canPerform, think, autoExecute,
                               run.Tools, session.Token, run.DraftYaml, principal.DisplayName, run.RelayLeaf,
                               run.Shared, run.Style))
            {
                var frame = TurnFrames.From(
                    ev, principal, _pending, ttl, session.ConversationId, ref proposalSeq);
                if (frame is null)
                    continue;
                if (ev.Kind == AssistantEventKind.Confirmation && frame.Payload is CommandProposedEvent proposed)
                    session.RecordStaged(ev.StagedConfirmation!, proposed.Token);

                // Before the frame is published, so the sentence is on its way to the engine while the
                // words are on their way to the reader.
                if (spoken is not null && frame.Name == TurnStream.TextDelta)
                    spoken.Take(((TokenEvent)frame.Payload).Text);

                // ⚠ A spoken turn is not over when its last word is written — the last sentence is
                // still being synthesised. The terminal frame is held until the audio has caught up,
                // because `done` is what a client tears its turn down on: emitted first, the sentences
                // still in flight would arrive for a turn that had already been closed.
                if (spoken is not null && frame.Name == TurnStream.Done)
                {
                    ending = frame;
                    continue;
                }

                Publish(session, frame);
            }

            // The last sentence, and the wait for the audio to catch up with the words. Inside the try
            // because a cancelled turn has nothing left to say.
            if (spoken is not null) await spoken.FinishAsync();
            if (ending is not null) Publish(session, ending);
        }
        catch (OperationCanceledException) when (session.Token.IsCancellationRequested)
        {
            // A stop, or nobody left to watch. The agent has already recorded the turn with whatever it
            // had generated; the terminal frame below is what tells the surfaces it is over.
            Publish(session, new TurnFrame(
                TurnStream.Done,
                new DoneEvent(session.TextSoFar, DateTimeOffset.UtcNow, null, 0)));
        }
    }

    private bool ThinkFor(TurnSession session)
    {
        using var scope = _scopes.CreateScope();
        var conversations = scope.ServiceProvider.GetRequiredService<IConversationStore>();
        var llm = scope.ServiceProvider.GetRequiredService<IOptions<Llm.Backends.LlmBackendOptions>>();
        return conversations.GetPreferences(session.ConversationId).Think ?? llm.Value.Think;
    }

    /// <summary>Record the frame on the session, hand it to its direct consumers, and put it on the
    /// event stream of every surface attached to this conversation.</summary>
    private void Publish(TurnSession session, TurnFrame frame) =>
        session.Broadcast(frame, f => _bus.PublishToAttached(
            session.Principal.UserId, session.ChatId, new ConversationEvent(f.Name, f.Payload)));

    /// <summary>
    /// Restate what is waiting on a conversation. The queue is state rather than an event — a surface
    /// that just attached must see it too — so it rides the same attach snapshot and is re-sent whole
    /// whenever it changes, rather than as a delta somebody could miss.
    /// </summary>
    private void PublishQueue(TurnSession session)
    {
        var queued = Queued(session.ConversationId);
        var running = Running(session.ConversationId);
        _bus.PublishToAttached(
            session.Principal.UserId, session.ChatId,
            new ConversationEvent(
                ConversationStream.TurnQueue,
                new TurnQueueEvent(session.ChatId, running?.TurnId, queued)));
    }
}

/// <summary>`turn.queue` — what is running on a conversation and what is waiting behind it.</summary>
public sealed record TurnQueueEvent(
    string ConversationId, string? RunningTurnId, IReadOnlyList<QueuedTurn> Queued);
