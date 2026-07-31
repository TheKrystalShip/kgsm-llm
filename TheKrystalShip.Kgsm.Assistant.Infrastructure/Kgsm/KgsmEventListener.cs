using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Kgsm;

/// <summary>
/// Keeps the assistant's blueprint cache honest by listening to the engine's own events.
/// <para>
/// The assistant caches the blueprint catalog behind a TTL, so a blueprint edited anywhere else —
/// the Control Panel's library editor, another operator's CLI — would otherwise be answered from a
/// stale snapshot for the rest of the TTL. A blueprint write is the engine's to announce
/// (<c>blueprint_created</c> / <c>_updated</c> / <c>_removed</c>), and this drops the cache the
/// moment one arrives, so the next turn reads the new values.
/// </para>
/// <para>
/// Only a resident host runs this, and only with <c>KGSM:EventSocketPath</c> set: binding is
/// exclusive, so a one-shot CLI must never take the socket out from under the service.
/// </para>
/// </summary>
internal sealed class KgsmEventListener : IHostedService
{
    private readonly IEventService _events;
    private readonly IInventoryInvalidation _inventory;
    private readonly ILogger<KgsmEventListener> _logger;

    public KgsmEventListener(
        IEventService events,
        IInventoryInvalidation inventory,
        ILogger<KgsmEventListener> logger)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _events.RegisterHandler<BlueprintCreatedData>(e => OnBlueprintChanged("blueprint_created", e.BlueprintName));
        _events.RegisterHandler<BlueprintUpdatedData>(e => OnBlueprintChanged("blueprint_updated", e.BlueprintName));
        _events.RegisterHandler<BlueprintRemovedData>(e => OnBlueprintChanged("blueprint_removed", e.BlueprintName));

        // Binds the socket and starts the read loop. kgsm-lib re-binds on its own if the socket file
        // is removed underneath us, so a kgsm redeploy does not leave the assistant deaf.
        _events.Initialize();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Drops the whole inventory cache, not just the one blueprint: the cache holds the catalog as a
    /// single snapshot, so there is no per-name entry to evict. The next read re-lists from kgsm.
    /// </summary>
    private Task OnBlueprintChanged(string eventType, string blueprintName)
    {
        _logger.LogInformation(
            "kgsm {EventType} for {BlueprintName} — invalidating the blueprint cache", eventType, blueprintName);
        _inventory.Invalidate();
        return Task.CompletedTask;
    }
}
