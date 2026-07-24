using System.Threading.Channels;

using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant;

/// <summary>
/// Ambient, per-turn sink for progress a long-running tool narrates WHILE it is still executing
/// (e.g. <c>create_blueprint</c>'s research→feasibility→draft→install→verify→teardown stages), so
/// an SSE client can render a live stepper instead of waiting silently for the tool's single
/// terminal result.
/// <para>
/// Mirrors <see cref="IConfirmationContext"/>'s ambient-scope shape exactly, for the same reason:
/// the tool dispatcher (and, for <c>create_blueprint</c>, the aggregator underneath it) calls
/// <see cref="Report"/> from deep inside tool execution with no per-turn context threaded through
/// it. <see cref="BeginTurn"/> opens the scope carrying the SAME <see cref="ChannelWriter{T}"/>
/// <see cref="ServerAssistant"/> is already relaying mapped agent events into, so a step lands on
/// the stream the instant it is reported — no buffering, no waiting for the tool to return. Backed
/// by an <see cref="AsyncLocal{T}"/>, so concurrently-handled turns stay isolated.
/// </para>
/// <para>
/// A turn started without <see cref="BeginTurn"/> (the buffered, non-streaming
/// <see cref="IServerAssistant.RunAsync"/> path) has no ambient sink — <see cref="Report"/> is then
/// a silent no-op, so a long tool still runs to completion and returns its one terminal card, just
/// without the intermediate narration.
/// </para>
/// </summary>
public interface ITurnProgress
{
    /// <summary>Starts a fresh per-turn scope carrying <paramref name="sink"/>. Dispose to clear it.</summary>
    IDisposable BeginTurn(ChannelWriter<AssistantStreamEvent> sink);

    /// <summary>
    /// Reports one progress step for <paramref name="tool"/>, identified by a short machine
    /// <paramref name="key"/> (e.g. <c>"research"</c>) and a plain-language <paramref name="label"/>
    /// for display (e.g. <c>"Looking it up online…"</c>). Reported when the step BEGINS. A no-op
    /// outside a turn opened with <see cref="BeginTurn"/>.
    /// </summary>
    void Report(Tool tool, string key, string label);
}

/// <inheritdoc />
public sealed class TurnProgress : ITurnProgress
{
    private static readonly AsyncLocal<ChannelWriter<AssistantStreamEvent>?> Current = new();

    public IDisposable BeginTurn(ChannelWriter<AssistantStreamEvent> sink)
    {
        Current.Value = sink;
        return new Scope();
    }

    public void Report(Tool tool, string key, string label)
    {
        // TryWrite is synchronous and always succeeds on the unbounded channel ServerAssistant opens
        // for a streaming turn, so a synchronous call site deep inside tool dispatch (no async context
        // to thread a Task through) can report a step inline, with no await.
        Current.Value?.TryWrite(AssistantStreamEvent.Progress(tool, key, label));
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose() => Current.Value = null;
    }
}
