using Microsoft.Extensions.Logging;

using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Service.Kgsm;

/// <summary>
/// Satisfies the assistant's <see cref="IServerOperations"/> port by calling KGSM.Lib's
/// <see cref="IInstanceService"/> directly. We depend on the instance service (which shells
/// out to kgsm via a process runner) rather than the full <c>IKgsmClient</c> ON PURPOSE:
/// constructing <c>IKgsmClient</c> auto-starts KGSM.Lib's Unix-socket event listener, which
/// would contend with the Discord bot for the single kgsm event socket. This service ingests
/// events over the HTTP webhook instead, so it must never bind that socket.
/// <para>
/// The KGSM.Lib instance service is synchronous, so calls are offloaded with
/// <see cref="Task.Run(Action)"/>. Per the port contract these never throw — failures map
/// to a failed <see cref="Result"/>.
/// </para>
/// </summary>
internal sealed class KgsmServerOperations : IServerOperations
{
    private readonly IInstanceService _instances;
    private readonly ILogger<KgsmServerOperations> _logger;

    public KgsmServerOperations(IInstanceService instances, ILogger<KgsmServerOperations> logger)
    {
        _instances = instances;
        _logger = logger;
    }

    public Task<Result> StartAsync(string instance, CancellationToken cancellationToken = default) =>
        RunAsync(nameof(StartAsync), instance, () => _instances.Start(instance), cancellationToken);

    public Task<Result> StopAsync(string instance, CancellationToken cancellationToken = default) =>
        RunAsync(nameof(StopAsync), instance, () => _instances.Stop(instance), cancellationToken);

    public Task<Result> RestartAsync(string instance, CancellationToken cancellationToken = default) =>
        RunAsync(nameof(RestartAsync), instance, () => _instances.Restart(instance), cancellationToken);

    public Task<Result> CreateBackupAsync(string instance, CancellationToken cancellationToken = default) =>
        RunAsync(nameof(CreateBackupAsync), instance, () => _instances.CreateBackup(instance), cancellationToken);

    public Task<Result> UpdateAsync(string instance, CancellationToken cancellationToken = default) =>
        RunAsync(nameof(UpdateAsync), instance, () => _instances.Update(instance), cancellationToken);

    public async Task<Result<string>> GetStatusAsync(string instance, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await Task.Run(() => _instances.GetStatus(instance), cancellationToken);
            return result.IsSuccess
                ? Result.Success(result.Stdout ?? string.Empty)
                : Result.Failure<string>(result.Stderr ?? "unknown error");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetStatus failed for {Instance}", instance);
            return Result.Failure<string>(ex.Message);
        }
    }

    public async Task<Result<bool>> IsActiveAsync(string instance, CancellationToken cancellationToken = default)
    {
        try
        {
            var active = await Task.Run(() => _instances.IsActive(instance), cancellationToken);
            return Result.Success(active);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IsActive failed for {Instance}", instance);
            return Result.Failure<bool>(ex.Message);
        }
    }

    public async Task<Result> InstallAsync(string blueprint, string? instanceName, CancellationToken cancellationToken = default)
    {
        try
        {
            // Long-running; completion is also broadcast via events. Mirror the bot: run it
            // and report queued-successfully unless it throws synchronously.
            await Task.Run(() => _instances.Install(blueprint, null, null, instanceName), cancellationToken);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Install failed for blueprint {Blueprint} (name={Name})", blueprint, instanceName);
            return Result.Failure(ex.Message);
        }
    }

    public async Task<Result> UninstallAsync(string instance, CancellationToken cancellationToken = default)
    {
        try
        {
            await Task.Run(() => _instances.Uninstall(instance), cancellationToken);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Uninstall failed for {Instance}", instance);
            return Result.Failure(ex.Message);
        }
    }

    private async Task<Result> RunAsync(
        string op, string instance, Func<KgsmResult> action, CancellationToken cancellationToken)
    {
        try
        {
            var result = await Task.Run(action, cancellationToken);
            if (result.IsSuccess)
                return Result.Success();

            _logger.LogWarning("{Op} failed for {Instance}: {Error}", op, instance, result.Stderr);
            return Result.Failure(result.Stderr ?? "unknown error");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Op} threw for {Instance}", op, instance);
            return Result.Failure(ex.Message);
        }
    }
}
