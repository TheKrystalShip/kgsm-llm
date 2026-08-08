namespace TheKrystalShip.Kgsm.Assistant.Service.Streaming;

/// <summary>
/// Ends turns nobody is around for. A turn deliberately outlives the surface that asked for it — that
/// is what lets a phone lock its screen while a desktop watches the reply — but it does not outlive its
/// person. The grace window is what separates leaving from a screen locking or a network changing
/// hands, and it is why this checks periodically rather than reacting to a disconnect.
/// </summary>
internal sealed class TurnPresenceWorker(ITurnRegistry turns, ILogger<TurnPresenceWorker> log)
    : BackgroundService
{
    /// <summary>How long a person may be entirely absent before their running turn is stopped.</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation(
            "Turn presence: a running turn is stopped once its person has been away for {Grace}s",
            Grace.TotalSeconds);

        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                turns.SweepAbandoned(Grace);
            }
            catch (Exception ex)
            {
                // A sweep that throws must not take the worker with it — the next tick tries again.
                log.LogError(ex, "Turn presence sweep failed");
            }
        }
    }
}
