using System.Runtime.CompilerServices;

using TheKrystalShip.KGSM.Lifecycle;
using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Service;

/// <summary>
/// The model backend, measured by being used.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a decorator and not a probe.</b> The chat model is socket-activated: connecting to its
/// endpoint is what <em>loads</em> it, and the proxy in front unloads it after an idle timeout to give
/// back the VRAM. A liveness probe on a timer would reset that timer before it could ever expire, pin
/// the model resident forever, and defeat the on-demand design — to answer a question every turn
/// already answers. A turn is the measurement.
/// </para>
/// <para>
/// <b>Why here and not at the turn.</b> A turn fails for reasons that are not the backend: a tool that
/// threw, an iteration cap, somebody pressing stop. Reporting <c>llm-backend</c> degraded on any of
/// those would be a fabricated diagnosis. This wraps the one call that actually reaches the model, so
/// what it reports is what it measured.
/// </para>
/// <para>
/// ⚠ <b>Registered by the resident service alone.</b> The CLI and the benchmark compose the same
/// backend and are not leaves — neither has a journal, and an eval run would report a dead backend
/// against a host whose backend is fine.
/// </para>
/// </remarks>
public sealed class MeasuredLlmClient(ILlmClient inner, LeafLifecycle lifecycle) : ILlmClient
{
    /// <summary>
    /// What the model answering, or failing to, says about the backend.
    /// </summary>
    /// <remarks>
    /// A cancelled call is not a failure — somebody pressed stop, or the turn was abandoned — and it is
    /// not a success either: nothing reached the model. Neither is reported, because the honest reading
    /// of a call that did not complete is no reading at all.
    /// </remarks>
    private void Reached() => lifecycle.MarkRecovered(AssistantComponents.LlmBackend);

    private void Failed(Exception ex) => lifecycle.MarkDegraded(
        AssistantComponents.LlmBackend,
        $"the model backend did not answer ({ex.Message}); the assistant accepts turns and can finish "
        + "none of them, while every other part of it reads healthy");

    private void Failed(string error) => lifecycle.MarkDegraded(
        AssistantComponents.LlmBackend,
        $"the model backend did not answer ({error}); the assistant accepts turns and can finish none "
        + "of them, while every other part of it reads healthy");

    public async Task<Result<LlmResponse>> ChatAsync(
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmToolDefinition>? tools = null,
        bool think = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Result<LlmResponse> result =
                await inner.ChatAsync(messages, tools, think, cancellationToken).ConfigureAwait(false);

            if (result.IsSuccess)
                Reached();
            else
                Failed(result.Error ?? "no reason given");

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Failed(ex);
            throw;
        }
    }

    /// <summary>
    /// The streaming counterpart.
    /// </summary>
    /// <remarks>
    /// ⚠ The reading is taken on the <b>first</b> chunk, not on the last. A stream that begins and is
    /// then abandoned — the consumer stops enumerating, the turn is cancelled — reached the backend
    /// perfectly well, and waiting for an end that never comes would leave a working backend reported
    /// as broken until the next turn happened to finish.
    /// </remarks>
    public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmToolDefinition>? tools = null,
        bool think = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IAsyncEnumerator<LlmStreamChunk> chunks =
            inner.ChatStreamAsync(messages, tools, think, cancellationToken).GetAsyncEnumerator(cancellationToken);

        bool reached = false;

        try
        {
            while (true)
            {
                LlmStreamChunk chunk;

                try
                {
                    if (!await chunks.MoveNextAsync().ConfigureAwait(false))
                        break;

                    chunk = chunks.Current;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Failed(ex);
                    throw;
                }

                if (!reached)
                {
                    reached = true;
                    Reached();
                }

                yield return chunk;
            }
        }
        finally
        {
            await chunks.DisposeAsync().ConfigureAwait(false);
        }
    }
}
