using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Kgsm;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Tests;

/// <summary>
/// Covers the pieces of <see cref="KgsmServerOperations"/> with real logic (the rest just
/// forwards to KGSM.Lib): the <see cref="TheKrystalShip.KGSM.Core.Interfaces.IInstanceFiles"/>
/// outcome→<see cref="Result"/> mapping for the three file methods (the jail itself lives in
/// kgsm-lib and is tested exhaustively there — <c>InstanceFilesTests</c> — not here), and the
/// fleet-status mapping that must preserve the measured-vs-unavailable distinction.
/// </summary>
public sealed class KgsmServerOperationsTests
{
    private readonly IInstanceService _instances = Substitute.For<IInstanceService>();
    private readonly IInstanceFiles _files = Substitute.For<IInstanceFiles>();
    private readonly ISystemService _system = Substitute.For<ISystemService>();
    private readonly IWatcherService _watcher = Substitute.For<IWatcherService>();
    private readonly IWatchdogClient _watchdog = Substitute.For<IWatchdogClient>();
    private readonly AsyncLocalInvocationContext _invocation = new();

    // The real shipped catalog, so a refusal that points the model at another tool names it the way
    // the file does — the same resolution production uses.
    private static readonly IToolCatalog Catalog = new DiskToolCatalog(ShippedPrompts.Directory);

    private KgsmServerOperations Create() =>
        new(_instances, _files, _system, _watcher, _watchdog, _invocation, Catalog,
            NullLogger<KgsmServerOperations>.Instance);

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
    /// The jail itself (traversal/symlink/special-file/atomic-write/.kgsmbak) is kgsm-lib's —
    /// covered exhaustively by <c>InstanceFilesTests</c> there. These tests only assert that
    /// <see cref="KgsmServerOperations"/> calls <see cref="IInstanceFiles"/> with the right
    /// arguments and maps every <see cref="FileOpOutcome"/> to the right <see cref="Result"/>.
    /// </summary>

    // --- ReadInstanceFileAsync (IInstanceFiles.Read outcome → Result<string>) ---

    [Fact]
    public async Task ReadInstanceFile_Ok_ReturnsContent()
    {
        _files.Read("inst", "inst.config.ini", Arg.Any<long>())
            .Returns(FileOpResult<FileContent>.Ok(new FileContent { Content = "port = 25565\n" }));

        var result = await Create().ReadInstanceFileAsync("inst", "inst.config.ini");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("port = 25565\n");
    }

    [Fact]
    public async Task ReadInstanceFile_BlankPath_DefaultsToInstanceConfigIni()
    {
        _files.Read("inst", "inst.config.ini", Arg.Any<long>())
            .Returns(FileOpResult<FileContent>.Ok(new FileContent { Content = "port = 1\n" }));

        var result = await Create().ReadInstanceFileAsync("inst", relativePath: "");

        result.IsSuccess.Should().BeTrue();
        _files.Received(1).Read("inst", "inst.config.ini", Arg.Any<long>());
    }

    [Fact]
    public async Task ReadInstanceFile_PassesA64KbCap()
    {
        _files.Read(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>())
            .Returns(FileOpResult<FileContent>.Ok(new FileContent { Content = "x" }));

        await Create().ReadInstanceFileAsync("inst", "inst.config.ini");

        _files.Received(1).Read("inst", "inst.config.ini", 64 * 1024);
    }

    [Fact]
    public async Task ReadInstanceFile_Binary_SucceedsWithPlaceholder_NeverDumpsBytes()
    {
        _files.Read("inst", "blob.bin", Arg.Any<long>())
            .Returns(FileOpResult<FileContent>.Fail(FileOpOutcome.Binary));

        var result = await Create().ReadInstanceFileAsync("inst", "blob.bin");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Contain("binary file");
    }

    [Fact]
    public async Task ReadInstanceFile_NotFound_Fails()
    {
        _files.Read("inst", "missing.ini", Arg.Any<long>())
            .Returns(FileOpResult<FileContent>.Fail(FileOpOutcome.NotFound));

        var result = await Create().ReadInstanceFileAsync("inst", "missing.ini");

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task ReadInstanceFile_OutOfJail_Fails()
    {
        _files.Read("inst", "../outside/secret.txt", Arg.Any<long>())
            .Returns(FileOpResult<FileContent>.Fail(FileOpOutcome.OutOfJail));

        var result = await Create().ReadInstanceFileAsync("inst", "../outside/secret.txt");

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task ReadInstanceFile_NotAFile_Fails()
    {
        _files.Read("inst", "pipe.sock", Arg.Any<long>())
            .Returns(FileOpResult<FileContent>.Fail(FileOpOutcome.NotAFile));

        var result = await Create().ReadInstanceFileAsync("inst", "pipe.sock");

        result.IsSuccess.Should().BeFalse();
        (result.Error ?? string.Empty).Should().Contain("regular file");
    }

    [Fact]
    public async Task ReadInstanceFile_TooLarge_Fails()
    {
        _files.Read("inst", "huge.log", Arg.Any<long>())
            .Returns(FileOpResult<FileContent>.Fail(FileOpOutcome.TooLarge));

        var result = await Create().ReadInstanceFileAsync("inst", "huge.log");

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task ReadInstanceFile_UnknownInstance_Fails()
    {
        _files.Read("ghost", "ghost.config.ini", Arg.Any<long>())
            .Returns(FileOpResult<FileContent>.Fail(FileOpOutcome.InstanceUnavailable));

        var result = await Create().ReadInstanceFileAsync("ghost", "ghost.config.ini");

        result.IsSuccess.Should().BeFalse();
    }

    // --- ListInstanceDirectoryAsync (IInstanceFiles.List outcome → Result<IReadOnlyList<InstanceDirEntry>>) ---

    [Fact]
    public async Task ListInstanceDirectory_Ok_MapsEntries()
    {
        _files.List("inst", null, Arg.Any<int>()).Returns(FileOpResult<DirListing>.Ok(new DirListing
        {
            Entries =
            [
                new FileEntry("logs", FileKind.Dir, null, null),
                new FileEntry("inst.config.ini", FileKind.File, 42, DateTimeOffset.UtcNow),
            ],
        }));

        var result = await Create().ListInstanceDirectoryAsync("inst");

        result.IsSuccess.Should().BeTrue();
        var byName = result.Value!.ToDictionary(e => e.Name);
        byName["logs"].IsDirectory.Should().BeTrue();
        byName["inst.config.ini"].IsDirectory.Should().BeFalse();
        byName["inst.config.ini"].Size.Should().Be(42);
    }

    [Fact]
    public async Task ListInstanceDirectory_PassesA200EntryCap()
    {
        _files.List(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(FileOpResult<DirListing>.Ok(new DirListing()));

        await Create().ListInstanceDirectoryAsync("inst", "logs");

        _files.Received(1).List("inst", "logs", 200);
    }

    [Fact]
    public async Task ListInstanceDirectory_NotFound_Fails()
    {
        _files.List("inst", "ghost-dir", Arg.Any<int>())
            .Returns(FileOpResult<DirListing>.Fail(FileOpOutcome.NotFound));

        var result = await Create().ListInstanceDirectoryAsync("inst", "ghost-dir");

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task ListInstanceDirectory_NotADirectory_Fails()
    {
        _files.List("inst", "inst.config.ini", Arg.Any<int>())
            .Returns(FileOpResult<DirListing>.Fail(FileOpOutcome.NotADirectory));

        var result = await Create().ListInstanceDirectoryAsync("inst", "inst.config.ini");

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task ListInstanceDirectory_OutOfJail_Fails()
    {
        _files.List("inst", "../outside", Arg.Any<int>())
            .Returns(FileOpResult<DirListing>.Fail(FileOpOutcome.OutOfJail));

        var result = await Create().ListInstanceDirectoryAsync("inst", "../outside");

        result.IsSuccess.Should().BeFalse();
    }

    // --- WriteInstanceFileAsync (IInstanceFiles.Write outcome → Result; fixed WriteOptions) ---

    [Fact]
    public async Task WriteInstanceFile_Ok_Succeeds()
    {
        _files.Write(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<WriteOptions>())
            .Returns(FileOpResult<FileStat>.Ok(new FileStat()));

        var result = await Create().WriteInstanceFileAsync("inst", "server.properties", "port=25565\n");

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task WriteInstanceFile_PassesAllowCreateBackupAndA10MbCap_LastWriterWins()
    {
        _files.Write(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<WriteOptions>())
            .Returns(FileOpResult<FileStat>.Ok(new FileStat()));

        await Create().WriteInstanceFileAsync("inst", "PalWorldSettings.ini", "new content");

        _files.Received(1).Write("inst", "PalWorldSettings.ini", "new content", Arg.Is<WriteOptions>(o =>
            o.AllowCreate && o.Backup && o.MaxBytes == 10 * 1024 * 1024 && o.ExpectedEtag == null));
    }

    [Fact]
    public async Task WriteInstanceFile_NestedPalworldStylePath_ForwardedVerbatim()
    {
        // Regression flavor: the relative path (working-dir-relative, deeply nested) must reach
        // IInstanceFiles unmodified — no local re-combine/re-jail on this side any more.
        const string relPath = "install/Pal/Saved/Config/LinuxServer/PalWorldSettings.ini";
        _files.Write("inst", relPath, Arg.Any<string>(), Arg.Any<WriteOptions>())
            .Returns(FileOpResult<FileStat>.Ok(new FileStat()));

        var result = await Create().WriteInstanceFileAsync(
            "inst", relPath, "[/Script/Pal.PalGameWorldSettings]");

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task WriteInstanceFile_NotFound_MissingParentOrNoCreate_Fails()
    {
        _files.Write("inst", "newdir/settings.ini", Arg.Any<string>(), Arg.Any<WriteOptions>())
            .Returns(FileOpResult<FileStat>.Fail(FileOpOutcome.NotFound));

        var result = await Create().WriteInstanceFileAsync("inst", "newdir/settings.ini", "content");

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task WriteInstanceFile_OutOfJail_Fails()
    {
        _files.Write("inst", "../outside/evil.txt", Arg.Any<string>(), Arg.Any<WriteOptions>())
            .Returns(FileOpResult<FileStat>.Fail(FileOpOutcome.OutOfJail));

        var result = await Create().WriteInstanceFileAsync("inst", "../outside/evil.txt", "pwned");

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task WriteInstanceFile_NotAFile_Fails()
    {
        _files.Write("inst", "pipe.sock", Arg.Any<string>(), Arg.Any<WriteOptions>())
            .Returns(FileOpResult<FileStat>.Fail(FileOpOutcome.NotAFile));

        var result = await Create().WriteInstanceFileAsync("inst", "pipe.sock", "pwned");

        result.IsSuccess.Should().BeFalse();
        (result.Error ?? string.Empty).Should().Contain("regular file");
    }

    [Fact]
    public async Task WriteInstanceFile_Binary_RefusesToClobberExistingBinary()
    {
        _files.Write("inst", "blob.bin", Arg.Any<string>(), Arg.Any<WriteOptions>())
            .Returns(FileOpResult<FileStat>.Fail(FileOpOutcome.Binary));

        var result = await Create().WriteInstanceFileAsync("inst", "blob.bin", "pwned");

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task WriteInstanceFile_TooLarge_Fails()
    {
        _files.Write("inst", "big.txt", Arg.Any<string>(), Arg.Any<WriteOptions>())
            .Returns(FileOpResult<FileStat>.Fail(FileOpOutcome.TooLarge));

        var huge = new string('a', 10 * 1024 * 1024 + 1); // one byte over the 10 MB cap

        var result = await Create().WriteInstanceFileAsync("inst", "big.txt", huge);

        result.IsSuccess.Should().BeFalse();
        (result.Error ?? string.Empty).Should().Contain("MB limit");
    }

    [Fact]
    public async Task WriteInstanceFile_UnknownInstance_Fails()
    {
        _files.Write("ghost", "x.txt", Arg.Any<string>(), Arg.Any<WriteOptions>())
            .Returns(FileOpResult<FileStat>.Fail(FileOpOutcome.InstanceUnavailable));

        var result = await Create().WriteInstanceFileAsync("ghost", "x.txt", "content");

        result.IsSuccess.Should().BeFalse();
    }

    // --- PrepareInstanceFileEditAsync (read the file, apply ONE replacement, refuse anything unclear) ---

    private const string Config =
        "[/Script/Pal.PalGameWorldSettings]\n" +
        "OptionSettings=(Difficulty=None,ExpRate=1.000000,bIsMultiplay=False,DeathPenalty=All)\n";

    private void FileHolds(string path, string content) =>
        _files.Read("inst", path, Arg.Any<long>())
            .Returns(FileOpResult<FileContent>.Ok(new FileContent { Content = content }));

    [Fact]
    public async Task PrepareEdit_AppliesTheReplacementToTheFileOnDisk()
    {
        FileHolds("PalWorldSettings.ini", Config);

        var result = await Create().PrepareInstanceFileEditAsync(
            "inst", "PalWorldSettings.ini", "Difficulty=None", "Difficulty=Difficulty_Hard");

        result.IsSuccess.Should().BeTrue();
        // Only the named text moved: every other setting comes off disk, never through the caller.
        result.Value.Should().Be(Config.Replace("Difficulty=None", "Difficulty=Difficulty_Hard"));
    }

    [Fact]
    public async Task PrepareEdit_ReadsUnderItsOwnCap_NotTheModelFacingReadCap()
    {
        FileHolds("PalWorldSettings.ini", Config);

        await Create().PrepareInstanceFileEditAsync("inst", "PalWorldSettings.ini", "ExpRate=1.000000", "ExpRate=2.000000");

        _files.Received(1).Read("inst", "PalWorldSettings.ini", 1024 * 1024);
    }

    [Fact]
    public async Task PrepareEdit_WritesNothing()
    {
        FileHolds("PalWorldSettings.ini", Config);

        await Create().PrepareInstanceFileEditAsync("inst", "PalWorldSettings.ini", "Difficulty=None", "Difficulty=Difficulty_Hard");

        _files.DidNotReceive().Write(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<WriteOptions>());
    }

    [Fact]
    public async Task PrepareEdit_AnchorMatchesNowhere_Fails()
    {
        FileHolds("PalWorldSettings.ini", Config);

        var result = await Create().PrepareInstanceFileEditAsync(
            "inst", "PalWorldSettings.ini", "bIsMultipla=True", "bIsMultiplay=True");

        result.IsSuccess.Should().BeFalse();
        (result.Error ?? string.Empty).Should().Contain("not in");
    }

    [Fact]
    public async Task PrepareEdit_AnchorMatchesSeveralPlaces_Fails()
    {
        FileHolds("server.properties", "pvp=true\nspawn-npcs=true\nallow-flight=true\n");

        var result = await Create().PrepareInstanceFileEditAsync(
            "inst", "server.properties", "=true", "=false");

        result.IsSuccess.Should().BeFalse();
        (result.Error ?? string.Empty).Should().Contain("more than one place");
    }

    [Fact]
    public async Task PrepareEdit_NoAnchorAndNoSeed_Fails()
    {
        FileHolds("server.properties", "pvp=true\n");

        var result = await Create().PrepareInstanceFileEditAsync("inst", "server.properties", "", "pvp=false");

        result.IsSuccess.Should().BeFalse();
        (result.Error ?? string.Empty).Should().Contain("old_string");
    }

    [Fact]
    public async Task PrepareEdit_ReplacementIdenticalToTheAnchor_Fails()
    {
        FileHolds("PalWorldSettings.ini", Config);

        var result = await Create().PrepareInstanceFileEditAsync(
            "inst", "PalWorldSettings.ini", "Difficulty=None", "Difficulty=None");

        result.IsSuccess.Should().BeFalse();
        (result.Error ?? string.Empty).Should().Contain("identical");
    }

    [Fact]
    public async Task PrepareEdit_AnEmptyFile_PointsAtTheReferenceFile()
    {
        FileHolds("PalWorldSettings.ini", "\n");

        var result = await Create().PrepareInstanceFileEditAsync(
            "inst", "PalWorldSettings.ini", "Difficulty=None", "Difficulty=Difficulty_Hard");

        result.IsSuccess.Should().BeFalse();
        (result.Error ?? string.Empty).Should().Contain("empty").And.Contain("copy_from");
    }

    [Fact]
    public async Task PrepareEdit_MissingTarget_PointsAtTheReferenceFile()
    {
        _files.Read("inst", "PalWorldSettings.ini", Arg.Any<long>())
            .Returns(FileOpResult<FileContent>.Fail(FileOpOutcome.NotFound));

        var result = await Create().PrepareInstanceFileEditAsync(
            "inst", "PalWorldSettings.ini", "Difficulty=None", "Difficulty=Difficulty_Hard");

        result.IsSuccess.Should().BeFalse();
        (result.Error ?? string.Empty).Should().Contain("copy_from");
    }

    [Fact]
    public async Task PrepareEdit_SeedsFromTheReferenceFile_AndAppliesTheEditToIt()
    {
        FileHolds("DefaultPalWorldSettings.ini", Config);

        var result = await Create().PrepareInstanceFileEditAsync(
            "inst", "PalWorldSettings.ini", "Difficulty=None", "Difficulty=Difficulty_Hard",
            copyFromPath: "DefaultPalWorldSettings.ini");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(Config.Replace("Difficulty=None", "Difficulty=Difficulty_Hard"));
        // The target was never read — the reference file IS the source of the proposal.
        _files.DidNotReceive().Read("inst", "PalWorldSettings.ini", Arg.Any<long>());
    }

    [Fact]
    public async Task PrepareEdit_SeedWithNoAnchor_CopiesTheReferenceVerbatim()
    {
        FileHolds("DefaultPalWorldSettings.ini", Config);

        var result = await Create().PrepareInstanceFileEditAsync(
            "inst", "PalWorldSettings.ini", "", "", copyFromPath: "DefaultPalWorldSettings.ini");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(Config);
    }

    [Fact]
    public async Task PrepareEdit_MissingSeedFile_Fails()
    {
        _files.Read("inst", "DefaultPalWorldSettings.ini", Arg.Any<long>())
            .Returns(FileOpResult<FileContent>.Fail(FileOpOutcome.NotFound));

        var result = await Create().PrepareInstanceFileEditAsync(
            "inst", "PalWorldSettings.ini", "a", "b", copyFromPath: "DefaultPalWorldSettings.ini");

        result.IsSuccess.Should().BeFalse();
        (result.Error ?? string.Empty).Should().Contain("nothing to copy from");
    }

    [Fact]
    public async Task PrepareEdit_OutOfJail_Fails()
    {
        _files.Read("inst", "../outside/secret.txt", Arg.Any<long>())
            .Returns(FileOpResult<FileContent>.Fail(FileOpOutcome.OutOfJail));

        var result = await Create().PrepareInstanceFileEditAsync("inst", "../outside/secret.txt", "a", "b");

        result.IsSuccess.Should().BeFalse();
        (result.Error ?? string.Empty).Should().Contain("outside the instance directory");
    }

    [Fact]
    public async Task PrepareEdit_Binary_Fails()
    {
        _files.Read("inst", "blob.bin", Arg.Any<long>())
            .Returns(FileOpResult<FileContent>.Fail(FileOpOutcome.Binary));

        var result = await Create().PrepareInstanceFileEditAsync("inst", "blob.bin", "a", "b");

        result.IsSuccess.Should().BeFalse();
        (result.Error ?? string.Empty).Should().Contain("text file");
    }

    [Fact]
    public async Task PrepareEdit_TooLarge_Fails()
    {
        _files.Read("inst", "huge.log", Arg.Any<long>())
            .Returns(FileOpResult<FileContent>.Fail(FileOpOutcome.TooLarge));

        var result = await Create().PrepareInstanceFileEditAsync("inst", "huge.log", "a", "b");

        result.IsSuccess.Should().BeFalse();
        (result.Error ?? string.Empty).Should().Contain("edit limit");
    }

    [Fact]
    public async Task PrepareEdit_UnknownInstance_Fails()
    {
        _files.Read("ghost", "x.txt", Arg.Any<long>())
            .Returns(FileOpResult<FileContent>.Fail(FileOpOutcome.InstanceUnavailable));

        var result = await Create().PrepareInstanceFileEditAsync("ghost", "x.txt", "a", "b");

        result.IsSuccess.Should().BeFalse();
        (result.Error ?? string.Empty).Should().Contain("not a known instance");
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

        // Measured-or-unknown at the Service boundary: a could-not-read instance is
        // Unavailable with Running=null — never a fabricated "stopped" (false).
        byName["broken"].Availability.Should().Be(FleetStatusAvailability.Unavailable);
        byName["broken"].Running.Should().BeNull();
        byName["broken"].Reason.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// The read succeeded and carried no run state — the engine measures an instance behind an
    /// unmounted library as an absence. That lands in the same place a failed read does, because
    /// nothing measured whether the server is up, and the reason names the disk.
    /// </summary>
    [Fact]
    public async Task GetFleetStatus_UnmeasuredRunState_IsUnavailableNamingTheOfflineLibrary()
    {
        _instances.GetAllStatuses(Arg.Any<bool>()).Returns(new Dictionary<string, Reading<InstanceRuntimeStatus>>
        {
            ["away"] = Reading<InstanceRuntimeStatus>.Measured(new InstanceRuntimeStatus
            {
                InstanceName = "away",
                Status = null,
                LibraryState = InstanceLibraryState.Offline,
            }),
            ["nostate"] = Reading<InstanceRuntimeStatus>.Measured(
                new InstanceRuntimeStatus { InstanceName = "nostate", Status = null }),
        });

        var result = await Create().GetFleetStatusAsync();

        result.IsSuccess.Should().BeTrue();
        var byName = result.Value!.ToDictionary(e => e.Instance);

        byName["away"].Availability.Should().Be(FleetStatusAvailability.Unavailable);
        byName["away"].Running.Should().BeNull();
        byName["away"].Reason.Should().Contain("disk is not mounted");

        // No library state to explain it: still unknown, and still not "stopped".
        byName["nostate"].Availability.Should().Be(FleetStatusAvailability.Unavailable);
        byName["nostate"].Running.Should().BeNull();
        byName["nostate"].Reason.Should().Contain("nothing measured it");
    }

    /// <summary>
    /// The health snapshot carries the absent run state through as absent, and does not probe ports
    /// for it: an instance nothing could read binds nothing the probe would find, and asking would
    /// turn "we could not look" into "they are not up".
    /// </summary>
    [Fact]
    public async Task GetHealthSnapshot_UnmeasuredRunState_CarriesItThroughAndSkipsThePortProbe()
    {
        _instances.GetInstanceStatus("away").Returns(new InstanceRuntimeStatus
        {
            InstanceName = "away",
            Status = null,
            LibraryState = InstanceLibraryState.Offline,
        });

        var result = await Create().GetHealthSnapshotAsync("away");

        result.IsSuccess.Should().BeTrue();
        result.Value!.Running.Should().BeNull();
        result.Value!.LibraryState.Should().Be(ServerLibraryState.Offline);
        result.Value!.PortsReachable.Should().BeNull();
        result.Value!.Restart.Should().BeNull();
        _watcher.DidNotReceive().TestPortWatch(Arg.Any<string>());
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

    [Theory]
    [InlineData(0, true, null)]     // all configured ports active
    [InlineData(1, false, null)]    // running but ports not bound
    [InlineData(44, null, "no ports configured")] // EC_WATCHER_PORT_NOT_ACTIVE → not applicable (skip)
    public async Task GetHealthSnapshot_Running_MapsPortProbeExitCode(
        int exitCode, bool? expectedReachable, string? expectedDetail)
    {
        _instances.GetInstanceStatus("factorio").Returns(new InstanceRuntimeStatus
        {
            InstanceName = "factorio",
            Status = true, // running → the probe runs
            Version = new VersionInfo { Current = "1.0.0", Checked = false, UpdatesAvailable = null },
            RecentLogs = "",
        });
        _watcher.TestPortWatch("factorio").Returns(new KgsmResult(exitCode));

        var s = (await Create().GetHealthSnapshotAsync("factorio")).Value!;

        s.PortsReachable.Should().Be(expectedReachable);
        s.PortsDetail.Should().Be(expectedDetail);
        _watcher.Received(1).TestPortWatch("factorio");
    }

    [Fact]
    public async Task GetHealthSnapshot_Stopped_DoesNotProbePorts()
    {
        _instances.GetInstanceStatus("factorio").Returns(new InstanceRuntimeStatus
        {
            InstanceName = "factorio",
            Status = false, // stopped → binding is meaningless, so no probe
            Version = new VersionInfo { Current = "1.0.0", Checked = false, UpdatesAvailable = null },
            RecentLogs = "",
        });

        var s = (await Create().GetHealthSnapshotAsync("factorio")).Value!;

        s.PortsReachable.Should().BeNull();
        _watcher.DidNotReceive().TestPortWatch(Arg.Any<string>());
    }

    [Fact]
    public async Task GetHealthSnapshot_NullStatus_Fails()
    {
        _instances.GetInstanceStatus("ghost").Returns((InstanceRuntimeStatus?)null);

        var result = await Create().GetHealthSnapshotAsync("ghost");

        result.IsSuccess.Should().BeFalse();
    }

    // --- PrepareInstanceSettingEditAsync (address the setting by key, read its value off disk) ---

    [Fact]
    public async Task PrepareSettingEdit_ChangesTheValueAndReportsWhatItReplaced()
    {
        FileHolds("PalWorldSettings.ini", Config);

        var result = await Create().PrepareInstanceSettingEditAsync(
            "inst", "PalWorldSettings.ini", "ExpRate", "2.000000");

        result.IsSuccess.Should().BeTrue();
        result.Value!.PreviousValue.Should().Be("1.000000");
        result.Value.NewValue.Should().Be("2.000000");
        result.Value.Content.Should().Be(Config.Replace("ExpRate=1.000000", "ExpRate=2.000000"));
    }

    [Fact]
    public async Task PrepareSettingEdit_WritesNothing()
    {
        FileHolds("PalWorldSettings.ini", Config);

        await Create().PrepareInstanceSettingEditAsync("inst", "PalWorldSettings.ini", "ExpRate", "2.000000");

        _files.DidNotReceive().Write("inst", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<WriteOptions>());
    }

    [Fact]
    public async Task PrepareSettingEdit_UnknownSetting_PointsAtSearchFiles()
    {
        FileHolds("PalWorldSettings.ini", Config);

        var result = await Create().PrepareInstanceSettingEditAsync(
            "inst", "PalWorldSettings.ini", "NoSuchSetting", "1");

        result.IsSuccess.Should().BeFalse();
        (result.Error ?? string.Empty).Should().Contain("search_instance_files");
    }

    [Fact]
    public async Task PrepareSettingEdit_SettingInTwoPlaces_RefusesRatherThanPickingOne()
    {
        FileHolds("server.properties", "difficulty=easy\ndifficulty=hard\n");

        var result = await Create().PrepareInstanceSettingEditAsync(
            "inst", "server.properties", "difficulty", "peaceful");

        result.IsSuccess.Should().BeFalse();
        (result.Error ?? string.Empty).Should().Contain("more than one place");
    }

    // --- the seeded-write target guard (a guessed directory must fail loudly, not create a file) ---

    [Fact]
    public async Task PrepareSettingEdit_SeededIntoADirectoryThatDoesNotExist_Refuses()
    {
        // The model reached the seeded path only because the real one failed to read, so a parent that
        // is not there means it guessed. Creating the file would satisfy the call and change nothing
        // the game reads.
        _files.List("inst", "Config/Linux_server", Arg.Any<int>())
            .Returns(FileOpResult<DirListing>.Fail(FileOpOutcome.NotFound));

        var result = await Create().PrepareInstanceSettingEditAsync(
            "inst", "Config/Linux_server/PalWorldSettings.ini", "ExpRate", "2.000000",
            copyFromPath: "DefaultPalWorldSettings.ini");

        result.IsSuccess.Should().BeFalse();
        (result.Error ?? string.Empty).Should().Contain("find_instance_file");
        _files.DidNotReceive().Read("inst", "DefaultPalWorldSettings.ini", Arg.Any<long>());
    }

    [Fact]
    public async Task PrepareEdit_SeededIntoADirectoryThatDoesNotExist_Refuses()
    {
        _files.List("inst", "Config/Linux_server", Arg.Any<int>())
            .Returns(FileOpResult<DirListing>.Fail(FileOpOutcome.NotFound));

        var result = await Create().PrepareInstanceFileEditAsync(
            "inst", "Config/Linux_server/PalWorldSettings.ini", "", "",
            copyFromPath: "DefaultPalWorldSettings.ini");

        result.IsSuccess.Should().BeFalse();
        (result.Error ?? string.Empty).Should().Contain("find_instance_file");
    }

    [Fact]
    public async Task PrepareSettingEdit_SeededIntoADirectoryThatExists_Proceeds()
    {
        _files.List("inst", "Config/LinuxServer", Arg.Any<int>())
            .Returns(FileOpResult<DirListing>.Ok(new DirListing { Entries = [] }));
        FileHolds("DefaultPalWorldSettings.ini", Config);

        var result = await Create().PrepareInstanceSettingEditAsync(
            "inst", "Config/LinuxServer/PalWorldSettings.ini", "ExpRate", "2.000000",
            copyFromPath: "DefaultPalWorldSettings.ini");

        result.IsSuccess.Should().BeTrue();
        result.Value!.PreviousValue.Should().Be("1.000000");
    }

    [Fact]
    public async Task PrepareSettingEdit_UnseededEditIsNotGuarded()
    {
        // Only a seeded write reads one file and targets another. An ordinary edit already proves the
        // target exists by reading it, so it must not pay for a directory listing.
        FileHolds("Config/LinuxServer/PalWorldSettings.ini", Config);

        var result = await Create().PrepareInstanceSettingEditAsync(
            "inst", "Config/LinuxServer/PalWorldSettings.ini", "ExpRate", "2.000000");

        result.IsSuccess.Should().BeTrue();
        _files.DidNotReceive().List("inst", Arg.Any<string>(), Arg.Any<int>());
    }
}
