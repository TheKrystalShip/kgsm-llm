using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Kgsm.Assistant.Service.Kgsm;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

/// <summary>
/// Covers the two pieces of <see cref="KgsmServerOperations"/> with real logic
/// (the rest just forwards to KGSM.Lib): the path-bound config read
/// (<see cref="KgsmServerOperations.ReadInstanceFileAsync"/>) and the fleet-status
/// mapping that must preserve the measured-vs-unavailable distinction.
/// </summary>
public sealed class KgsmServerOperationsTests : IDisposable
{
    private readonly IInstanceService _instances = Substitute.For<IInstanceService>();
    private readonly string _root;     // stand-in for an instance install dir
    private readonly string _outside;  // a sibling tree the read must never reach

    public KgsmServerOperationsTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "kgsm-ops-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(baseDir, "inst");
        _outside = Path.Combine(baseDir, "outside");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private KgsmServerOperations Create() =>
        new(_instances, NullLogger<KgsmServerOperations>.Instance);

    private void StubInstall(string instance, string? installDir) =>
        _instances.GetInstanceInfo(instance).Returns(new Instance { Name = instance, InstallDir = installDir ?? string.Empty });

    [Fact]
    public async Task ReadInstanceFile_ValidFileInInstallDir_ReturnsContent()
    {
        StubInstall("inst", _root);
        await File.WriteAllTextAsync(Path.Combine(_root, "inst.config.ini"), "port = 25565\n");

        var result = await Create().ReadInstanceFileAsync("inst", "inst.config.ini");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Contain("port = 25565");
    }

    [Fact]
    public async Task ReadInstanceFile_UnknownInstance_Fails()
    {
        _instances.GetInstanceInfo("ghost").Returns((Instance?)null);

        var result = await Create().ReadInstanceFileAsync("ghost", "ghost.config.ini");

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task ReadInstanceFile_EmptyInstallDir_Fails_NoCwdRelativeRead()
    {
        // The CWD-relative-read guard: an empty install dir must never combine into
        // a path rooted at the service's working directory.
        StubInstall("inst", installDir: "");

        var result = await Create().ReadInstanceFileAsync("inst", "inst.config.ini");

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task ReadInstanceFile_MissingFile_Fails()
    {
        StubInstall("inst", _root); // dir exists, file does not

        var result = await Create().ReadInstanceFileAsync("inst", "inst.config.ini");

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task ReadInstanceFile_DotDotEscape_IsRefused()
    {
        StubInstall("inst", _root);
        await File.WriteAllTextAsync(Path.Combine(_outside, "secret.txt"), "TOPSECRET");

        var result = await Create().ReadInstanceFileAsync("inst", "../outside/secret.txt");

        result.IsSuccess.Should().BeFalse();
        (result.Value ?? string.Empty).Should().NotContain("TOPSECRET");
    }

    [Fact]
    public async Task ReadInstanceFile_SiblingDirWithSharedPrefix_IsRefused()
    {
        // /…/inst must not admit /…/inst-evil — the trailing-separator prefix guard.
        var sibling = _root + "-evil";
        Directory.CreateDirectory(sibling);
        await File.WriteAllTextAsync(Path.Combine(sibling, "secret.txt"), "TOPSECRET");
        StubInstall("inst", _root);

        var result = await Create().ReadInstanceFileAsync("inst", "../inst-evil/secret.txt");

        result.IsSuccess.Should().BeFalse();
        (result.Value ?? string.Empty).Should().NotContain("TOPSECRET");
    }

    [Fact]
    public async Task ReadInstanceFile_SymlinkPointingOutside_IsRefused()
    {
        var secret = Path.Combine(_outside, "secret.txt");
        await File.WriteAllTextAsync(secret, "TOPSECRET");
        // A symlink that lives INSIDE the install dir but points OUT of it.
        File.CreateSymbolicLink(Path.Combine(_root, "inst.config.ini"), secret);
        StubInstall("inst", _root);

        var result = await Create().ReadInstanceFileAsync("inst", "inst.config.ini");

        result.IsSuccess.Should().BeFalse();
        (result.Value ?? string.Empty).Should().NotContain("TOPSECRET");
    }

    [Fact]
    public async Task GetFleetStatus_PreservesMeasuredVsUnavailable()
    {
        _instances.GetAllStatuses(Arg.Any<bool>()).Returns(new Dictionary<string, Reading<InstanceRuntimeStatus>>
        {
            ["up"] = Reading<InstanceRuntimeStatus>.Measured(
                new InstanceRuntimeStatus { InstanceName = "up", Status = true }),
            ["down"] = Reading<InstanceRuntimeStatus>.Measured(
                new InstanceRuntimeStatus { InstanceName = "down", Status = false }),
            ["broken"] = Reading<InstanceRuntimeStatus>.Unavailable(
                "boom", ReadingCode.RequiresRegeneration),
        });

        var result = await Create().GetFleetStatusAsync();

        result.IsSuccess.Should().BeTrue();
        var byName = result.Value!.ToDictionary(e => e.Instance);

        byName["up"].Availability.Should().Be(FleetStatusAvailability.Read);
        byName["up"].Running.Should().BeTrue();

        byName["down"].Availability.Should().Be(FleetStatusAvailability.Read);
        byName["down"].Running.Should().BeFalse();

        // The §3.7 guard at the Service boundary: a could-not-read instance is
        // Unavailable with Running=null — never a fabricated "stopped" (false).
        byName["broken"].Availability.Should().Be(FleetStatusAvailability.Unavailable);
        byName["broken"].Running.Should().BeNull();
        byName["broken"].Reason.Should().NotBeNullOrEmpty();
    }
}
