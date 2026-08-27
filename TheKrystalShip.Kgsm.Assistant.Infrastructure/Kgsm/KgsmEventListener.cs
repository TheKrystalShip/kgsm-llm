using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Kgsm;

/// <summary>
/// Keeps the assistant's inventory caches honest by listening to the engine's own events.
/// <para>
/// The assistant caches the instance roster and the blueprint catalog behind a TTL, so a server
/// installed or a blueprint edited anywhere else — the Control Panel, the Discord bot, another
/// operator's CLI, or this process's own confirmed action — would otherwise be answered from a stale
/// snapshot for the rest of the TTL. Every one of those writes is the engine's to announce, and this
/// drops the matching cache the moment an announcement arrives, so the next turn reads the new world.
/// </para>
/// <para>
/// The journal is the right seam precisely because it is surface-blind: it carries a change made by
/// anyone on the host, not only the ones this process performed, so there is one freshness path
/// rather than one per surface that can mutate the inventory. Nothing invalidates from inside a
/// mutating tool — that would be the same staleness with a narrower trigger, fresh for the
/// assistant's own actions and stale for everybody else's.
/// </para>
/// <para>
/// Events are the mechanism and the TTL is the backstop, and both are load-bearing: an event drops
/// the cache the moment the world changes, and the TTL bounds how long a missed one can be believed.
/// </para>
/// <para>
/// Only a resident host runs this, and only with <c>KGSM:JournalDir</c> set — because a one-shot
/// CLI has no cache to keep warm, not because it would collide with anything. The journal is a
/// file: readers need no coordination, so a CLI run beside the service would be harmless.
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
        _events.RegisterHandler<BlueprintCreatedData>(e => OnBlueprintChanged("blueprint.created", e.BlueprintName));
        _events.RegisterHandler<BlueprintUpdatedData>(e => OnBlueprintChanged("blueprint.updated", e.BlueprintName));
        _events.RegisterHandler<BlueprintRemovedData>(e => OnBlueprintChanged("blueprint.removed", e.BlueprintName));

        // The four moments `kgsm instances list` answers differently: the record appearing and the
        // install succeeding, the record going and the uninstall succeeding. Both halves of each pair
        // are handled because the phase event is when the roster actually changes on disk and the fact
        // event is when the change is known to have worked — a cache that dropped on only one of them
        // is stale for the window between them. Invalidation is idempotent and the refresh is lazy, so
        // the pair costs one re-read, not two.
        _events.RegisterHandler<InstanceCreatedData>(e => OnInstanceRosterChanged("server.install.created", e.InstanceName));
        _events.RegisterHandler<InstanceInstalledData>(e => OnInstanceRosterChanged("server.installed", e.InstanceName));
        _events.RegisterHandler<InstanceRemovedData>(e => OnInstanceRosterChanged("server.uninstall.removed", e.InstanceName));
        _events.RegisterHandler<InstanceUninstalledData>(e => OnInstanceRosterChanged("server.uninstalled", e.InstanceName));

        // An update rewrites the instance's own record, so the roster snapshot describing it is a
        // reading from before the change. Both the run ending and the version actually moving are
        // handled: the engine emits the first for every update and the second only when the version
        // differs, and a cache that waited for the second would hold a stale record through every
        // update that reinstalled the same version.
        _events.RegisterHandler<InstanceUpdatedData>(e => OnInstanceRosterChanged("server.update.completed", e.InstanceName));
        _events.RegisterHandler<InstanceVersionUpdatedData>(
            e => OnInstanceRosterChanged("server.updated", e.InstanceName));

        // Starts the journal read loop. kgsm-lib tolerates a journal directory that does not exist
        // yet and picks up the first segment when it appears, so a kgsm redeploy — or a host that
        // has never emitted an event — never leaves the assistant deaf.
        _events.Initialize();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Drops the whole blueprint catalog, not just the one blueprint: the cache holds the catalog as a
    /// single snapshot, so there is no per-name entry to evict. The next read re-lists from kgsm.
    /// </summary>
    private Task OnBlueprintChanged(string eventType, string blueprintName)
    {
        _logger.LogInformation(
            "kgsm {EventType} for {BlueprintName} — invalidating the blueprint cache", eventType, blueprintName);
        _inventory.InvalidateBlueprints();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Drops the instance roster, and only it: what games exist is a separate question from what is
    /// installed, and the blueprint catalog did not change.
    /// </summary>
    private Task OnInstanceRosterChanged(string eventType, string instanceName)
    {
        _logger.LogInformation(
            "kgsm {EventType} for {InstanceName} — invalidating the instance cache", eventType, instanceName);
        _inventory.InvalidateInstances();
        return Task.CompletedTask;
    }
}
