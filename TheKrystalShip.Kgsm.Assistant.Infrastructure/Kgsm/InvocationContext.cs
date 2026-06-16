namespace TheKrystalShip.Kgsm.Assistant.Infrastructure;

/// <summary>
/// The provenance of the action currently being performed — <em>who</em> asked
/// (<see cref="Actor"/>, e.g. the verified Discord principal <c>discord:Haru</c> or the terminal
/// user <c>cli:heisen</c>) and <em>through which surface</em> (<see cref="Origin"/>). Flows to KGSM as
/// <c>$KGSM_EVENT_ACTOR</c>/<c>$KGSM_EVENT_ORIGIN</c> so the audit event the engine emits is attributable.
/// Host-neutral: the HTTP service stamps it from the OAuth principal, the CLI from the OS user — both
/// share this one provenance shape, each host picking the factory for its surface.
/// </summary>
public sealed record Invocation(string? Actor, string Origin)
{
    /// <summary>The assistant (Discord/web) surface tag (architecture.html §3·d origin set).</summary>
    public const string AssistantOrigin = "assistant";

    /// <summary>The terminal surface tag (architecture.html §3·d origin set).</summary>
    public const string CliOrigin = "cli";

    /// <summary>Build an invocation for the signed-in assistant user — <c>actor = discord:&lt;displayName&gt;</c>,
    /// <c>origin = assistant</c>. A blank name yields a null actor (KGSM keeps its OS-user fallback, never a
    /// fabricated identity).</summary>
    public static Invocation ForAssistant(string? displayName) =>
        new(string.IsNullOrWhiteSpace(displayName) ? null : $"discord:{displayName}", AssistantOrigin);

    /// <summary>Build an invocation for the terminal user — <c>actor = cli:&lt;osUser&gt;</c>,
    /// <c>origin = cli</c>. A blank name yields a null actor (KGSM keeps its OS-user fallback, never a
    /// fabricated identity).</summary>
    public static Invocation ForCli(string? osUser) =>
        new(string.IsNullOrWhiteSpace(osUser) ? null : $"cli:{osUser}", CliOrigin);
}

/// <summary>
/// Ambient holder of the current <see cref="Invocation"/>, set at a host entry point (the HTTP
/// service's <c>/turn</c>/<c>/confirm</c> from the authenticated principal, or the CLI once per
/// process from the OS user) and read at the kgsm chokepoint (<c>KgsmServerOperations</c>) so every
/// mutating call stamps provenance — without threading an actor through the assistant agent loop or
/// the shared <c>IServerOperations</c> port.
/// </summary>
/// <remarks>
/// Backed by an <see cref="System.Threading.AsyncLocal{T}"/>, so the value flows down the awaited call
/// chain of the request that set it and is isolated per concurrent request. <see cref="Begin"/> returns a
/// scope that restores the previous value on dispose. <see cref="Current"/> is <see langword="null"/>
/// outside any scope — the chokepoint then passes null and KGSM applies its honest fallback.
/// </remarks>
public interface IInvocationContext
{
    Invocation? Current { get; }
    IDisposable Begin(Invocation invocation);
}

/// <inheritdoc cref="IInvocationContext"/>
public sealed class AsyncLocalInvocationContext : IInvocationContext
{
    private static readonly AsyncLocal<Invocation?> _current = new();

    public Invocation? Current => _current.Value;

    public IDisposable Begin(Invocation invocation)
    {
        Invocation? previous = _current.Value;
        _current.Value = invocation;
        return new Scope(previous);
    }

    private sealed class Scope(Invocation? previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _current.Value = previous;
        }
    }
}
