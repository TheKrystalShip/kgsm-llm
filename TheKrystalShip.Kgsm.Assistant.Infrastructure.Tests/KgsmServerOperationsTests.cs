using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Kgsm;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Tests;

/// <summary>
/// Covers the two pieces of <see cref="KgsmServerOperations"/> with real logic
/// (the rest just forwards to KGSM.Lib): the path-bound config read
/// (<see cref="KgsmServerOperations.ReadInstanceFileAsync"/>) and the fleet-status
/// mapping that must preserve the measured-vs-unavailable distinction.
/// </summary>
public sealed class KgsmServerOperationsTests : IDisposable
{
    private readonly IInstanceService _instances = Substitute.For<IInstanceService>();
    private readonly ISystemService _system = Substitute.For<ISystemService>();
    private readonly AsyncLocalInvocationContext _invocation = new();
    private readonly string _root;     // stand-in for an instance (config) directory
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
        new(_instances, _system, _invocation, NullLogger<KgsmServerOperations>.Instance);

    // --- provenance: a turn/confirm scope stamps actor (the Discord principal) + origin=assistant ---

    [Fact]
    public async Task StartAsync_WithinInvocationScope_StampsActorAndAssistantOrigin()
    {
        _instances.Start(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(new KgsmResult(0, "ok"));
        KgsmServerOperations ops = Create();

        using (_invocation.Begin(Invocation.ForAssistant("Haru")))
            await ops.StartAsync("inst");

        _instances.Received(1).Start("inst", "discord:Haru", "assistant");
    }

    [Fact]
    public async Task Mutations_WithinScope_AllCarryProvenance()
    {
        _instances.Update(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>()).Returns(new KgsmResult(0, "ok"));
        _instances.CreateBackup(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>()).Returns(new KgsmResult(0, "ok"));
        _instances.SetInstanceConfigValue(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>()).Returns(new KgsmResult(0, "ok"));
        _instances.Install(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>()).Returns(new KgsmResult(0, "ok"));
        KgsmServerOperations ops = Create();

        using (_invocation.Begin(Invocation.ForAssistant("Haru")))
        {
            await ops.UpdateAsync("inst");
            await ops.CreateBackupAsync("inst");
            await ops.SetInstanceConfigValueAsync("inst", "key", "val");
            await ops.InstallAsync("valheim", "my-server");
        }

        _instances.Received(1).Update("inst", "discord:Haru", "assistant");
        _instances.Received(1).CreateBackup("inst", "discord:Haru", "assistant");
        _instances.Received(1).SetInstanceConfigValue("inst", "key", "val", "discord:Haru", "assistant");
        _instances.Received(1).Install("valheim", null, null, "my-server", "discord:Haru", "assistant");
    }

    [Fact]
    public async Task StartAsync_OutsideAnyScope_PassesNullProvenance_KgsmKeepsItsFallback()
    {
        _instances.Start(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(new KgsmResult(0, "ok"));
        KgsmServerOperations ops = Create();

        await ops.StartAsync("inst"); // no Begin() scope

        _instances.Received(1).Start("inst", null, null); // honest-unknown, never fabricated
    }

    /// <summary>
    /// Stub kgsm's config-path resolution (<c>instances find</c> → __find_instance_config)
    /// to return the given absolute config-file path. The file's DIRECTORY becomes the read
    /// boundary — this mirrors the real engine, the same resolution config-get/config-set use.
    /// </summary>
    private void StubConfigPath(string instance, string configFilePath) =>
        _instances.FindConfigPath(instance).Returns(new KgsmResult(0, configFilePath));

    [Fact]
    public async Task ReadInstanceFile_ResolvedConfig_ReturnsContent()
    {
        StubConfigPath("inst", Path.Combine(_root, "inst.config.ini"));
        await File.WriteAllTextAsync(Path.Combine(_root, "inst.config.ini"), "port = 25565\n");

        var result = await Create().ReadInstanceFileAsync("inst", "inst.config.ini");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Contain("port = 25565");
    }

    [Fact]
    public async Task ReadInstanceFile_ReadsResolvedConfigDir_NotInstallDir()
    {
        // Regression guard for the view_config_file bug. Model the real layout: the config
        // lives in the working dir while install_dir is a DIFFERENT, deeper directory
        // (…/<inst>/install). The old code bound the read boundary to install_dir and so
        // 404'd for every real instance; resolving via kgsm's find-path reads the real file.
        var workingDir = Path.Combine(_root, "factorio-test");
        var installDir = Path.Combine(workingDir, "install");
        Directory.CreateDirectory(installDir);
        await File.WriteAllTextAsync(
            Path.Combine(workingDir, "factorio-test.config.ini"), "auto_update = false\n");

        // kgsm resolves the config to the working dir, never the install dir.
        StubConfigPath("factorio-test", Path.Combine(workingDir, "factorio-test.config.ini"));
        // Belt-and-suspenders: even if the impl regressed to GetInstanceInfo().InstallDir,
        // that points at the file-less install dir.
        _instances.GetInstanceInfo("factorio-test")
            .Returns(new Instance { Name = "factorio-test", InstallDir = installDir, WorkingDir = workingDir });

        var result = await Create().ReadInstanceFileAsync("factorio-test", "factorio-test.config.ini");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Contain("auto_update = false");
    }

    [Fact]
    public async Task ReadInstanceFile_UnknownInstance_Fails()
    {
        _instances.FindConfigPath("ghost").Returns(new KgsmResult(1, "", "Instance 'ghost' not found"));

        var result = await Create().ReadInstanceFileAsync("ghost", "ghost.config.ini");

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task ReadInstanceFile_EmptyResolvedPath_Fails_NoCwdRelativeRead()
    {
        // The CWD-relative-read guard: an empty resolved path must never combine into
        // a path rooted at the service's working directory.
        StubConfigPath("inst", string.Empty);

        var result = await Create().ReadInstanceFileAsync("inst", "inst.config.ini");

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task ReadInstanceFile_MissingFile_Fails()
    {
        StubConfigPath("inst", Path.Combine(_root, "inst.config.ini")); // dir exists, file does not

        var result = await Create().ReadInstanceFileAsync("inst", "inst.config.ini");

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task ReadInstanceFile_DotDotEscape_IsRefused()
    {
        StubConfigPath("inst", Path.Combine(_root, "inst.config.ini"));
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
        StubConfigPath("inst", Path.Combine(_root, "inst.config.ini"));

        var result = await Create().ReadInstanceFileAsync("inst", "../inst-evil/secret.txt");

        result.IsSuccess.Should().BeFalse();
        (result.Value ?? string.Empty).Should().NotContain("TOPSECRET");
    }

    [Fact]
    public async Task ReadInstanceFile_SymlinkPointingOutside_IsRefused()
    {
        var secret = Path.Combine(_outside, "secret.txt");
        await File.WriteAllTextAsync(secret, "TOPSECRET");
        // A symlink that lives INSIDE the instance dir but points OUT of it.
        File.CreateSymbolicLink(Path.Combine(_root, "inst.config.ini"), secret);
        StubConfigPath("inst", Path.Combine(_root, "inst.config.ini"));

        var result = await Create().ReadInstanceFileAsync("inst", "inst.config.ini");

        result.IsSuccess.Should().BeFalse();
        (result.Value ?? string.Empty).Should().NotContain("TOPSECRET");
    }

    [Fact]
    public async Task ReadInstanceFile_NonRegularFile_IsRefused()
    {
        // A FIFO inside the instance dir: opening it for read would block forever, so the
        // stat()-based guard must refuse it before opening. (Skips if mkfifo is unavailable.)
        var fifo = Path.Combine(_root, "pipe.sock");
        if (!TryMakeFifo(fifo)) return;

        StubConfigPath("inst", Path.Combine(_root, "inst.config.ini"));

        var result = await Create().ReadInstanceFileAsync("inst", "pipe.sock");

        result.IsSuccess.Should().BeFalse("a FIFO/socket isn't a regular file and would block on open");
        (result.Error ?? string.Empty).Should().Contain("regular file");
    }

    [Fact]
    public async Task ReadInstanceFile_BinaryFile_IsNotDumped()
    {
        // A NUL byte marks the content as binary → report the size, never spray raw bytes.
        StubConfigPath("inst", Path.Combine(_root, "inst.config.ini"));
        await File.WriteAllBytesAsync(
            Path.Combine(_root, "blob.bin"), new byte[] { 0x50, 0x4B, 0x00, 0x01, 0x02 });

        var result = await Create().ReadInstanceFileAsync("inst", "blob.bin");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Contain("binary file").And.Contain("bytes");
    }

    [Fact]
    public async Task ListInstanceDirectory_ListsEntries_DirsAndFilesWithinJail()
    {
        StubConfigPath("inst", Path.Combine(_root, "inst.config.ini"));
        await File.WriteAllTextAsync(Path.Combine(_root, "inst.config.ini"), "port = 1\n");
        Directory.CreateDirectory(Path.Combine(_root, "logs"));

        var result = await Create().ListInstanceDirectoryAsync("inst");

        result.IsSuccess.Should().BeTrue();
        var byName = result.Value!.ToDictionary(e => e.Name);
        byName.Should().ContainKey("inst.config.ini");
        byName["inst.config.ini"].IsDirectory.Should().BeFalse();
        byName.Should().ContainKey("logs");
        byName["logs"].IsDirectory.Should().BeTrue();
    }

    [Fact]
    public async Task ListInstanceDirectory_DotDotEscape_IsRefused()
    {
        StubConfigPath("inst", Path.Combine(_root, "inst.config.ini"));
        await File.WriteAllTextAsync(Path.Combine(_outside, "secret.txt"), "TOPSECRET");

        var result = await Create().ListInstanceDirectoryAsync("inst", "../outside");

        result.IsSuccess.Should().BeFalse();
    }

    /// <summary>Best-effort FIFO creation via the <c>mkfifo</c> CLI; false if unavailable.</summary>
    private static bool TryMakeFifo(string path)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start("mkfifo", path);
            p?.WaitForExit(2000);
            return File.Exists(path);
        }
        catch { return false; }
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

    // --- GetHealthSnapshot (fetch + map; judgment lives in the aggregator) ---

    [Fact]
    public async Task GetHealthSnapshot_MapsStatusLogsVersionAndDisk()
    {
        _instances.GetInstanceStatus("minecraft").Returns(new InstanceRuntimeStatus
        {
            InstanceName = "minecraft",
            Status = true,
            Version = new VersionInfo
            {
                Current = "1.20.1",
                Latest = "1.20.4",
                Checked = true,
                UpdatesAvailable = true,
            },
            RecentLogs = "INFO started\nERROR boom\n",
        });
        _system.GetSystemInfo().Returns(new SystemInfo
        {
            Disk = new DiskInfo { UsePercent = "26%", Size = "916G", Available = "649G" },
        });

        var result = await Create().GetHealthSnapshotAsync("minecraft");

        result.IsSuccess.Should().BeTrue();
        var s = result.Value!;
        s.Running.Should().BeTrue();
        s.RecentLogLines.Should().BeEquivalentTo(new[] { "INFO started", "ERROR boom" });
        s.UpdatesAvailable.Should().BeTrue();
        s.CurrentVersion.Should().Be("1.20.1");
        s.LatestVersion.Should().Be("1.20.4");
        s.HostDisk!.UsedPercent.Should().Be(26);     // "26%" parsed to an int
        s.HostDisk.Size.Should().Be("916G");
        s.HostDisk.Available.Should().Be("649G");
        s.HostDiskUnavailableReason.Should().BeNull();
    }

    [Fact]
    public async Task GetHealthSnapshot_HostDiskUnavailable_SetsReason_NeverFabricates()
    {
        _instances.GetInstanceStatus("minecraft").Returns(new InstanceRuntimeStatus
        {
            InstanceName = "minecraft",
            Status = true,
            Version = new VersionInfo { Current = "1.0.0", Checked = false, UpdatesAvailable = null },
            RecentLogs = "",
        });
        _system.GetSystemInfo().Returns((SystemInfo?)null); // host read failed

        var result = await Create().GetHealthSnapshotAsync("minecraft");

        result.IsSuccess.Should().BeTrue();
        var s = result.Value!;
        s.HostDisk.Should().BeNull();                          // no fabricated 0%
        s.HostDiskUnavailableReason.Should().NotBeNullOrEmpty();
        s.UpdatesAvailable.Should().BeNull();                  // honest unknown preserved
    }

    [Fact]
    public async Task GetHealthSnapshot_NullStatus_Fails()
    {
        _instances.GetInstanceStatus("ghost").Returns((InstanceRuntimeStatus?)null);

        var result = await Create().GetHealthSnapshotAsync("ghost");

        result.IsSuccess.Should().BeFalse();
    }
}
