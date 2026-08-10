using TheKrystalShip.Kgsm.Assistant.Envelope;

namespace TheKrystalShip.Kgsm.Assistant.Health;

/// <summary>
/// One health check's outcome. <see cref="State"/> is the pass/fail
/// judgment; <see cref="Severity"/> is how a surface weights it (e.g. a stopped server
/// is <see cref="CheckState.Pass"/> + <see cref="Severity.Info"/>, never a failure).
/// </summary>
/// <param name="Name">Short check name (e.g. "liveness", "logs", "updates", "disk").</param>
/// <param name="State">The pass/warn/fail/skip judgment.</param>
/// <param name="Severity">The rendered weight.</param>
/// <param name="Detail">Human-readable one-liner — also feeds the deterministic summary.</param>
public sealed record HealthCheck(string Name, CheckState State, Severity Severity, string Detail);

/// <summary>
/// The structured card payload for <c>run_health_check</c> — the deterministic sweep's
/// result. <see cref="Overall"/> is the worst <em>non-skip</em>
/// check; skipped checks are reported honestly (<see cref="Skipped"/>) and never inflate
/// the pass count. This is the <c>D</c> in <see cref="ToolResult{TData}"/>.
/// </summary>
/// <param name="Overall">The worst non-skip check state (the headline verdict).</param>
/// <param name="Checks">Every check run, in display order.</param>
/// <param name="Passed">How many checks passed.</param>
/// <param name="Total">How many checks were defined.</param>
/// <param name="Skipped">How many checks were not assessed (source unavailable / N/A).</param>
public sealed record HealthData(
    CheckState Overall,
    IReadOnlyList<HealthCheck> Checks,
    int Passed,
    int Total,
    int Skipped);
