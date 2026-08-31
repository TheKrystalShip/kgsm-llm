using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using TheKrystalShip.Kgsm.Assistant.Blueprints;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.KGSM.Cluster;

namespace TheKrystalShip.Kgsm.Assistant.Service;

/// <summary>
/// Startup-only sweep for orphaned <c>create_blueprint</c> test-install probes. A probe is normally torn down in
/// the aggregator's own guaranteed <c>finally</c> block — an orphan surviving here means the whole
/// process (not just the pipeline call) was killed mid-authoring, which is the one case the aggregator's
/// own teardown can never reach. Runs once at startup over <see cref="IServerInventory"/>'s cached
/// roster, then exits — a recurring poll would be needless (a process crash mid-authoring is rare, and
/// every ordinary exit path is already covered by the aggregator).
/// <para>
/// <b>A member of a cluster does not sweep.</b> Reaching a node means acting for somebody, and a
/// startup sweep has nobody behind it — every node would refuse it, and an answer of no servers would
/// read as no orphans. So it says it did not look rather than reporting that it found nothing, and an
/// orphan left by an interrupted authoring run stands until somebody removes it.
/// </para>
/// </summary>
internal sealed class BlueprintProbeSweepService : BackgroundService
{
    private readonly IServerInventory _inventory;
    private readonly IServerOperations _operations;
    private readonly ClusterOptions _cluster;
    private readonly ILogger<BlueprintProbeSweepService> _logger;

    public BlueprintProbeSweepService(
        IServerInventory inventory,
        IServerOperations operations,
        ClusterOptions cluster,
        ILogger<BlueprintProbeSweepService> logger)
    {
        _inventory = inventory;
        _operations = operations;
        _cluster = cluster;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => SweepOnceAsync(stoppingToken);

    /// <summary>The sweep's actual logic, factored out of <see cref="ExecuteAsync"/> so it can be driven
    /// directly and deterministically in tests (a <see cref="BackgroundService"/>'s Start/Stop timing is
    /// not a reliable way to await a one-shot body).</summary>
    internal async Task SweepOnceAsync(CancellationToken cancellationToken)
    {
        if (_cluster.Enabled)
        {
            _logger.LogInformation(
                "Not sweeping for orphaned blueprint-authoring probes: this assistant serves a cluster, " +
                "and reading a node requires a person to act for. An orphan from an interrupted " +
                "authoring run stands on the node it was installed on.");
            return;
        }

        try
        {
            var instances = await _inventory.GetInstancesAsync(cancellationToken);
            var orphans = instances.Keys.Where(BlueprintProbeNaming.IsProbe).ToList();
            if (orphans.Count == 0)
                return;

            _logger.LogWarning(
                "Found {Count} orphaned blueprint-authoring probe instance(s) — a prior run was interrupted " +
                "mid-pipeline. Sweeping: {Names}", orphans.Count, string.Join(", ", orphans));

            foreach (var name in orphans)
            {
                var result = await _operations.UninstallAsync(name, cancellationToken);
                if (!result.IsSuccess)
                    _logger.LogError("Failed to sweep orphaned probe '{Name}': {Error}", name, result.Error);
            }
        }
        catch (Exception ex)
        {
            // Never let a sweep failure block the service from starting — it retries on the next restart.
            _logger.LogError(ex, "Blueprint-authoring probe sweep failed");
        }
    }
}
