using Microsoft.Extensions.Logging;

namespace TheKrystalShip.Kgsm.Assistant.Relay;

/// <summary>
/// The assistant's liveness probe, as every consuming leaf needs to ask it: is it up right now?
/// </summary>
/// <remarks>
/// Shared because the answer decides whether a surface degrades, and two leaves disagreeing about
/// what "up" means is two leaves degrading differently for the same outage. It is deliberately the
/// weakest possible claim — a 2xx from <c>/health</c> — and never more: liveness is not readiness,
/// and a leaf that needs to know whether a turn will succeed finds that out by running it.
/// </remarks>
public static class AssistantHealthProbe
{
    /// <summary>
    /// The default budget. Short on purpose: a probe answers a question a caller is already waiting
    /// on, and a hung assistant must not become a hung caller.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Returns whether <c>GET /health</c> answers 2xx within the budget. Never throws and never
    /// blocks longer than <paramref name="timeout"/>; a timeout, an unreachable host and a non-2xx
    /// are all the same answer — not up — because none of them is a working assistant.
    /// </summary>
    /// <remarks>
    /// Bounded by its own linked token rather than by <see cref="HttpClient.Timeout"/>, so a client
    /// shared with the long-lived turn stream is not given a class-wide ceiling by a probe.
    /// </remarks>
    public static async Task<bool> CheckAsync(
        HttpClient client, CancellationToken ct, ILogger? logger = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(client);

        var budget = timeout ?? DefaultTimeout;
        using var timed = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timed.CancelAfter(budget);

        try
        {
            // Headers only: the status is the whole answer.
            using var response = await client
                .GetAsync("/health", HttpCompletionOption.ResponseHeadersRead, timed.Token)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger?.LogDebug("assistant /health probe timed out after {Timeout}", budget);
            return false;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "assistant /health probe failed");
            return false;
        }
    }
}
