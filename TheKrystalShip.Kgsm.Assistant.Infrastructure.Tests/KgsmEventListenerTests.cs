using FluentAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Infrastructure.Extensions;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Kgsm;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Events;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Tests;

/// <summary>
/// The listener's whole job is that a change made anywhere else on the host reaches this process's
/// caches. These assert that: each blueprint event drops the catalog, each roster event drops the
/// instance list, neither drops the other, and the journal is read only by a host that asked for it.
/// </summary>
public class KgsmEventListenerTests
{
    private readonly IEventService _events = Substitute.For<IEventService>();
    private readonly IInventoryInvalidation _inventory = Substitute.For<IInventoryInvalidation>();

    private KgsmEventListener Create() => new(_events, _inventory, NullLogger<KgsmEventListener>.Instance);

    [Fact]
    public async Task StartAsync_starts_listening()
    {
        await Create().StartAsync(CancellationToken.None);

        _events.Received(1).Initialize();
    }

    [Fact]
    public async Task Blueprint_created_invalidates_the_cache()
    {
        Func<BlueprintCreatedData, Task>? handler = null;
        _events.RegisterHandler(Arg.Do<Func<BlueprintCreatedData, Task>>(h => handler = h));

        await Create().StartAsync(CancellationToken.None);
        await handler!(new BlueprintCreatedData { BlueprintName = "factorio" });

        _inventory.Received(1).InvalidateBlueprints();
    }

    [Fact]
    public async Task Blueprint_updated_invalidates_the_cache()
    {
        Func<BlueprintUpdatedData, Task>? handler = null;
        _events.RegisterHandler(Arg.Do<Func<BlueprintUpdatedData, Task>>(h => handler = h));

        await Create().StartAsync(CancellationToken.None);
        await handler!(new BlueprintUpdatedData { BlueprintName = "factorio" });

        _inventory.Received(1).InvalidateBlueprints();
    }

    [Fact]
    public async Task Blueprint_removed_invalidates_the_cache()
    {
        Func<BlueprintRemovedData, Task>? handler = null;
        _events.RegisterHandler(Arg.Do<Func<BlueprintRemovedData, Task>>(h => handler = h));

        await Create().StartAsync(CancellationToken.None);
        await handler!(new BlueprintRemovedData { BlueprintName = "factorio" });

        _inventory.Received(1).InvalidateBlueprints();
    }

    /// <summary>
    /// The defect this listener exists to prevent, on the roster half: a server installed — by this
    /// process's own confirmed action or by any other surface on the host — is invisible to every
    /// later turn until the TTL lapses, so an uninstall staged against it cannot find it.
    /// </summary>
    [Fact]
    public async Task Instance_installed_invalidates_the_instance_cache()
    {
        Func<InstanceInstalledData, Task>? handler = null;
        _events.RegisterHandler(Arg.Do<Func<InstanceInstalledData, Task>>(h => handler = h));

        await Create().StartAsync(CancellationToken.None);
        await handler!(new InstanceInstalledData { InstanceName = "factorio-01", Blueprint = "factorio" });

        _inventory.Received(1).InvalidateInstances();
    }

    [Fact]
    public async Task Instance_created_invalidates_the_instance_cache()
    {
        Func<InstanceCreatedData, Task>? handler = null;
        _events.RegisterHandler(Arg.Do<Func<InstanceCreatedData, Task>>(h => handler = h));

        await Create().StartAsync(CancellationToken.None);
        await handler!(new InstanceCreatedData { InstanceName = "factorio-01", Blueprint = "factorio" });

        _inventory.Received(1).InvalidateInstances();
    }

    [Fact]
    public async Task Instance_uninstalled_invalidates_the_instance_cache()
    {
        Func<InstanceUninstalledData, Task>? handler = null;
        _events.RegisterHandler(Arg.Do<Func<InstanceUninstalledData, Task>>(h => handler = h));

        await Create().StartAsync(CancellationToken.None);
        await handler!(new InstanceUninstalledData { InstanceName = "factorio-01" });

        _inventory.Received(1).InvalidateInstances();
    }

    [Fact]
    public async Task Instance_removed_invalidates_the_instance_cache()
    {
        Func<InstanceRemovedData, Task>? handler = null;
        _events.RegisterHandler(Arg.Do<Func<InstanceRemovedData, Task>>(h => handler = h));

        await Create().StartAsync(CancellationToken.None);
        await handler!(new InstanceRemovedData { InstanceName = "factorio-01" });

        _inventory.Received(1).InvalidateInstances();
    }

    /// <summary>
    /// An update rewrites the instance's own record, so the roster describing it is a reading from
    /// before the change — the user's explicit third case alongside install and uninstall.
    /// </summary>
    [Fact]
    public async Task Instance_updated_invalidates_the_instance_cache()
    {
        Func<InstanceUpdatedData, Task>? handler = null;
        _events.RegisterHandler(Arg.Do<Func<InstanceUpdatedData, Task>>(h => handler = h));

        await Create().StartAsync(CancellationToken.None);
        await handler!(new InstanceUpdatedData { InstanceName = "factorio-01" });

        _inventory.Received(1).InvalidateInstances();
    }

    [Fact]
    public async Task Instance_version_updated_invalidates_the_instance_cache()
    {
        Func<InstanceVersionUpdatedData, Task>? handler = null;
        _events.RegisterHandler(Arg.Do<Func<InstanceVersionUpdatedData, Task>>(h => handler = h));

        await Create().StartAsync(CancellationToken.None);
        await handler!(new InstanceVersionUpdatedData
        {
            InstanceName = "factorio-01", OldVersion = "2.0.76", NewVersion = "2.0.77",
        });

        _inventory.Received(1).InvalidateInstances();
    }

    /// <summary>
    /// The two caches answer separate questions, so neither event drops the other's snapshot — a
    /// server installed does not change what games exist, and a blueprint edited does not change
    /// what is installed. Dropping both would cost the untouched half a kgsm subprocess for nothing.
    /// </summary>
    [Fact]
    public async Task Roster_and_catalog_events_do_not_drop_each_other()
    {
        Func<InstanceInstalledData, Task>? installed = null;
        Func<BlueprintUpdatedData, Task>? updated = null;
        _events.RegisterHandler(Arg.Do<Func<InstanceInstalledData, Task>>(h => installed = h));
        _events.RegisterHandler(Arg.Do<Func<BlueprintUpdatedData, Task>>(h => updated = h));

        await Create().StartAsync(CancellationToken.None);
        await installed!(new InstanceInstalledData { InstanceName = "factorio-01", Blueprint = "factorio" });
        await updated!(new BlueprintUpdatedData { BlueprintName = "factorio" });

        _inventory.Received(1).InvalidateInstances();
        _inventory.Received(1).InvalidateBlueprints();
        _inventory.DidNotReceive().Invalidate();
    }

    /// <summary>
    /// Binding a unix socket is exclusive, so a host that did not configure one — every CLI
    /// invocation — must register nothing. Otherwise a one-shot `kgsm-assistant-cli` run would take
    /// the socket away from the resident service for as long as it lived.
    /// </summary>
    [Fact]
    public void No_socket_path_registers_no_listener()
    {
        var services = new ServiceCollection();

        services.AddKgsmEventListener(Config(("KGSM:Path", "/usr/local/bin/kgsm")));

        services.Should().NotContain(d => d.ServiceType == typeof(IHostedService));
        services.Should().NotContain(d => d.ServiceType == typeof(IEventService));
    }

    [Fact]
    public void A_configured_socket_path_registers_the_listener()
    {
        var services = new ServiceCollection();

        services.AddKgsmEventListener(Config(
            ("KGSM:Path", "/usr/local/bin/kgsm"),
            ("KGSM:JournalDir", "/var/lib/kgsm/events")));

        services.Should().Contain(d => d.ImplementationType == typeof(KgsmEventListener));
        services.Should().Contain(d => d.ServiceType == typeof(IEventService));
    }

    private static IConfiguration Config(params (string key, string value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.key, v.value)))
            .Build();
}
