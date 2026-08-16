using System.Net.Sockets;

using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Infrastructure.Retrieval;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Search;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Kgsm.Assistant.Service.Configuration;
using TheKrystalShip.Llm.Models;
using TheKrystalShip.Rag.Index;
using TheKrystalShip.KGSM.Lifecycle;

namespace TheKrystalShip.Kgsm.Assistant.Service;

/// <summary>
/// What this leaf says about its own state, to its own journal.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>The half a probe cannot see.</b> <c>/health</c> answers that the pipeline is listening, which
/// is true of an assistant with a dead model backend, an unreadable retrieval index and a web-search
/// budget it has already spent — none of which stops it serving a perfectly cheerful <c>{"status":
/// "ok"}</c> while being unable to answer anything.
/// </para>
/// <para>
/// <b>The backend is measured by using it, not by pinging it.</b> The chat model is socket-activated:
/// connecting to its endpoint <em>loads</em> it, and the proxy in front of it unloads the model after
/// its idle timeout to give back the VRAM. A liveness probe on a timer would reset that timer before
/// it could ever expire and pin ~8.7GB resident forever — defeating the on-demand design outright to
/// answer a question every turn already answers. So a failed turn is the measurement, and the next
/// turn that succeeds is the recovery.
/// </para>
/// <para>
/// The one probe that is free is <see cref="ProbeResidentBackendAsync"/>: the model's own port is bound
/// only while it is loaded, and it is not the port activation listens on. Asking it costs one TCP
/// connect, starts nothing, and extends nothing.
/// </para>
/// <para>
/// ⚠ <b>It reports on itself only.</b> This leaf reads every producer's journal for its incident tools,
/// and none of that belongs here — a leaf stating something about another leaf in its own journal is
/// exactly the second answer able to disagree that producer-from-location exists to prevent.
/// </para>
/// </remarks>
public sealed class AssistantLifecycleReporter(
    IServiceProvider services,
    IHostApplicationLifetime lifetime,
    LeafLifecycle lifecycle,
    IOptions<AssistantServiceOptions> options,
    IConfiguration configuration,
    ILogger<AssistantLifecycleReporter> logger) : BackgroundService
{
    /// <summary>How often this leaf re-reads its own dependencies.</summary>
    /// <remarks>
    /// Slow: these are conditions that persist, and the emitter reports only transitions, so a steady
    /// state costs nothing after the first line.
    /// </remarks>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>Bounded so a hung backend cannot stall the reading that is meant to notice it.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // ⚠ Readiness is this leaf's own, not the host's. ApplicationStarted fires once every hosted
        // service has started, which for this one really is the moment it can do its job: the pipeline
        // is listening, the conversation store is open and the tool graph is composed. The leaves whose
        // real work begins later — a supervisor joining its slice, a gateway connecting — hang it off
        // their own signal instead, and this is the same choice made for this leaf's shape rather than
        // the same code.
        lifetime.ApplicationStarted.Register(() => lifecycle.MarkReady("serving"));

        lifetime.ApplicationStopping.Register(() => lifecycle.MarkStopping(LeafStopReason.Signal));

        using var timer = new PeriodicTimer(Interval);

        try
        {
            // The first reading is taken after one interval, never immediately. A reading taken at
            // startup races the things it measures and files this leaf's own boot as a fault.
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                await ReportAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task ReportAsync(CancellationToken ct)
    {
        try
        {
            ReportRetrieval();
            ReportConversationStore();
            ReportWebSearch();
            await ReportBackendAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Reporting must never take the surface down with it. A reading that threw is a reading not
            // taken, which the next tick retries.
            logger.LogDebug(ex, "could not read this leaf's own state");
        }
    }

    /// <summary>
    /// Whether the retrieval index can be read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The provider already knows: it returns a written reason for every way an index fails to load —
    /// not built yet, an incompatible build, unreadable, corrupt, a different embedding model. Nothing
    /// is probed here that it has not already measured.
    /// </para>
    /// <para>
    /// ⚠ <b>One RAG fault stays unreported, and must.</b> Changing the embedding backend does not change
    /// the model <em>name</em>, so an index built by a different embedder still passes the mismatch
    /// check and is served with vectors from the wrong vector space. Nothing on this host detects it.
    /// Reporting the index healthy is honest — it loaded and its header matched; claiming the vectors
    /// are current would not be.
    /// </para>
    /// </remarks>
    private void ReportRetrieval()
    {
        var provider = services.GetService<RagIndexProvider>();

        // Retrieval is optional. A host with none configured is not a degraded host.
        if (provider is null)
            return;

        Result<RagIndex> index = provider.Get();

        // ⚠ Success is not enough, and this is the case the whole component is here for: a failed
        // reload keeps serving the LAST GOOD index rather than going dark, so retrieval answers
        // perfectly while answering from a corpus that is no longer the one on disk. Nothing outside
        // this process can see that — the searches keep working and keep returning the old documents.
        if (provider.ServingLastGoodBecause is { } stale)
        {
            lifecycle.MarkDegraded(
                AssistantComponents.RagIndex,
                $"retrieval is answering from a previously loaded index ({stale}); searches still work "
                + "and return what the corpus said before the last rebuild");
            return;
        }

        if (index.IsSuccess)
            lifecycle.MarkRecovered(AssistantComponents.RagIndex);
        else
            lifecycle.MarkDegraded(
                AssistantComponents.RagIndex,
                $"the assistant answers from the model alone ({index.Error}); anything it was told "
                + "through the documentation is unavailable, and it will not say so unprompted");
    }

    /// <summary>
    /// Whether the store this leaf keeps conversations, sessions and staged actions in can be reached.
    /// </summary>
    /// <remarks>
    /// ⚠ Its loss is not visible from outside. The pipeline still listens and <c>/health</c> still
    /// answers, while no conversation can be read or written, no session can be checked, and no action
    /// can be staged or confirmed — an assistant that looks healthy and can do nothing.
    /// </remarks>
    private void ReportConversationStore()
    {
        string path = configuration["Conversation:DatabasePath"] ?? string.Empty;

        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            using FileStream probe = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            lifecycle.MarkRecovered(AssistantComponents.ConversationStore);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lifecycle.MarkDegraded(
                AssistantComponents.ConversationStore,
                $"this leaf's own store cannot be opened ({ex.Message}); no conversation can be read or "
                + "written, no session checked and no action staged, while the surface reads healthy");
        }
    }

    /// <summary>
    /// Whether the assistant can still look something up on the web today.
    /// </summary>
    /// <remarks>
    /// A capability loss that is invisible from outside: the assistant keeps answering, from the local
    /// index and the model alone, and a question whose answer is only online comes back as an honest
    /// "nothing found" that reads exactly like the thing not existing.
    /// </remarks>
    private void ReportWebSearch()
    {
        var budget = services.GetService<DailyCallBudget>();

        if (budget is null || !budget.Configured)
            return;

        if (budget.Remaining > 0)
            lifecycle.MarkRecovered(AssistantComponents.WebSearch);
        else
            lifecycle.MarkDegraded(
                AssistantComponents.WebSearch,
                "this host's daily web-search budget is spent; the assistant answers from the local "
                + "index and the model alone until it resets");
    }

    /// <summary>
    /// Whether the model backend answers — asked only when asking is free.
    /// </summary>
    /// <remarks>
    /// A closed internal port means the model is <b>unloaded</b>, which is its resting state and not a
    /// fault. Nothing is reported then: the turn that next needs it is the honest measurement, and
    /// saying anything here would be reporting on a question nobody asked.
    /// </remarks>
    private async Task ReportBackendAsync(CancellationToken ct)
    {
        int port = options.Value.Lifecycle.ResidentBackendPort;

        if (port <= 0)
            return;

        bool? resident = await ProbeResidentBackendAsync(port, ct).ConfigureAwait(false);

        if (resident is true)
            lifecycle.MarkRecovered(AssistantComponents.LlmBackend);
    }

    /// <summary>
    /// Whether the model is loaded, without loading it.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>This must be the model's own port, never the activating socket.</b> The two are different
    /// ports on purpose: systemd listens on one and starts the model on the first connection, and the
    /// model binds the other only once it is running. Pointed at the first, this probe would load ~8.7GB
    /// on every tick's worth of idleness and keep it there — so a host that collapses the two onto one
    /// port must leave <c>Lifecycle:ResidentBackendPort</c> at 0 rather than let this guess.
    /// </remarks>
    private static async Task<bool?> ProbeResidentBackendAsync(int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProbeTimeout);

            await client.ConnectAsync("127.0.0.1", port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The connect timed out rather than being refused, which is not "unloaded" — something is
            // holding the port without answering. Unknown, and never guessed at.
            return null;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}

/// <summary>
/// The parts of this leaf's job that can stop working while it keeps serving.
/// </summary>
/// <remarks>
/// ⚠ Deliberately a bounded set naming classes of thing. A component id built from a conversation, a
/// model name or an instance would make the emitter's dedup dictionary grow without limit; the
/// particulars belong in the detail.
/// </remarks>
public static class AssistantComponents
{
    /// <summary>The model server that answers a turn.</summary>
    public const string LlmBackend = "llm-backend";

    /// <summary>The retrieval index the <c>search</c> tool reads before it reaches for the web.</summary>
    public const string RagIndex = "rag-index";

    /// <summary>This leaf's own store: conversations, sessions and staged actions.</summary>
    public const string ConversationStore = "conversation-store";

    /// <summary>Looking something up online.</summary>
    public const string WebSearch = "web-search";

    /// <summary>Running an engine command.</summary>
    public const string Kgsm = "kgsm";

    /// <summary>Reading the federated journal this leaf's incident tools answer from.</summary>
    public const string EventListener = "event-listener";
}
