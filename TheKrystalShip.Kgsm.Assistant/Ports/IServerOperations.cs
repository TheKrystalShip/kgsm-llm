using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Ports;

/// <summary>
/// The mutating actions and live (non-cached) reads the assistant performs against
/// a running kgsm. The host implements this over whatever it already uses to talk
/// to kgsm (the Discord bot routes through its MediatR handlers; a standalone
/// service can call KGSM.Lib directly). Implementations must not throw — return a
/// failed <see cref="Result"/> instead.
/// <para>
/// install / uninstall NEVER flow through the agent loop: the dispatcher only
/// STAGES them for human confirmation. They live on this port for the confirm step
/// only — <see cref="IServerAssistant.ConfirmAsync"/> calls them after a human has
/// confirmed a staged operation, never the model.
/// </para>
/// </summary>
public interface IServerOperations
{
    Task<Result> StartAsync(string instance, CancellationToken cancellationToken = default);
    Task<Result> StopAsync(string instance, CancellationToken cancellationToken = default);
    Task<Result> RestartAsync(string instance, CancellationToken cancellationToken = default);
    Task<Result> CreateBackupAsync(string instance, CancellationToken cancellationToken = default);
    Task<Result> UpdateAsync(string instance, CancellationToken cancellationToken = default);

    /// <summary>Live status text for an instance.</summary>
    Task<Result<string>> GetStatusAsync(string instance, CancellationToken cancellationToken = default);

    /// <summary>Live running-or-not check for an instance.</summary>
    Task<Result<bool>> IsActiveAsync(string instance, CancellationToken cancellationToken = default);

    /// <summary>
    /// Installs a new instance from a blueprint. Called only by
    /// <see cref="IServerAssistant.ConfirmAsync"/> after a human confirms a staged
    /// install — never from the agent loop.
    /// </summary>
    /// <param name="blueprint">The resolved blueprint name to install from.</param>
    /// <param name="instanceName">Optional custom instance name; null lets kgsm name it.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    Task<Result> InstallAsync(string blueprint, string? instanceName, CancellationToken cancellationToken = default);

    /// <summary>
    /// PERMANENTLY uninstalls an instance and all its data. Called only by
    /// <see cref="IServerAssistant.ConfirmAsync"/> after a human confirms a staged
    /// uninstall — never from the agent loop.
    /// </summary>
    /// <param name="instance">The resolved instance name to uninstall.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    Task<Result> UninstallAsync(string instance, CancellationToken cancellationToken = default);
}
