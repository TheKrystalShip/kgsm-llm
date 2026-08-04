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
/// The listener's whole job is that a blueprint changed anywhere else on the host reaches this
/// process's cache. These assert the two halves of that: the engine's three blueprint events each
/// invalidate, and the socket is bound only by a host that asked for it.
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

        _inventory.Received(1).Invalidate();
    }

    [Fact]
    public async Task Blueprint_updated_invalidates_the_cache()
    {
        Func<BlueprintUpdatedData, Task>? handler = null;
        _events.RegisterHandler(Arg.Do<Func<BlueprintUpdatedData, Task>>(h => handler = h));

        await Create().StartAsync(CancellationToken.None);
        await handler!(new BlueprintUpdatedData { BlueprintName = "factorio" });

        _inventory.Received(1).Invalidate();
    }

    [Fact]
    public async Task Blueprint_removed_invalidates_the_cache()
    {
        Func<BlueprintRemovedData, Task>? handler = null;
        _events.RegisterHandler(Arg.Do<Func<BlueprintRemovedData, Task>>(h => handler = h));

        await Create().StartAsync(CancellationToken.None);
        await handler!(new BlueprintRemovedData { BlueprintName = "factorio" });

        _inventory.Received(1).Invalidate();
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
