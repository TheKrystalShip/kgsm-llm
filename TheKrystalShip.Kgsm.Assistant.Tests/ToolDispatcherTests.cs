using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Llm.Models;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// Verifies the dispatcher's routing, name resolution, and confirmation staging
/// against the host ports (<see cref="IServerOperations"/> / <see cref="IServerInventory"/>),
/// which the host implements over whatever it uses to talk to kgsm.
/// </summary>
public class ToolDispatcherTests
{
    private readonly IServerOperations _operations = Substitute.For<IServerOperations>();
    private readonly IServerInventory _inventory = Substitute.For<IServerInventory>();
    private readonly ConfirmationContext _confirmations = new();

    public ToolDispatcherTests()
    {
        // Two terraria-* instances (matched by substring / game type) plus a unique minecraft.
        var instances = new Dictionary<string, string>
        {
            ["terraria-pvp"] = "terraria",
            ["terraria-creative"] = "terraria",
            ["minecraft"] = "minecraft",
        };
        _inventory.GetInstancesAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<string, string>)instances);

        var blueprints = new[] { "valheim", "terraria" };
        _inventory.GetBlueprintNamesAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyCollection<string>)blueprints);
    }

    private ToolDispatcher Create() =>
        new(_operations, _inventory, _confirmations, NullLogger<ToolDispatcher>.Instance);

    private static LlmToolCall Call(string name, string instance) =>
        new(name, new Dictionary<string, string?> { ["instance_name"] = instance });

    private static LlmToolCall InstallCall(string blueprint, string? name = null) =>
        new(LlmTools.InstallServer, new Dictionary<string, string?>
        {
            ["blueprint_name"] = blueprint,
            ["instance_name"] = name,
        });

    [Fact]
    public async Task ExactName_Resolves_AndExecutes()
    {
        _operations.IsActiveAsync("minecraft", Arg.Any<CancellationToken>())
            .Returns(Result.Success(true));

        var result = await Create().ExecuteAsync(Call(LlmTools.IsServerActive, "minecraft"));

        result.Should().Contain("running");
        await _operations.Received(1).IsActiveAsync("minecraft", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SingleFuzzyMatch_Resolves()
    {
        _operations.IsActiveAsync("terraria-pvp", Arg.Any<CancellationToken>())
            .Returns(Result.Success(false));

        // "pvp" is a substring of exactly one instance.
        await Create().ExecuteAsync(Call(LlmTools.IsServerActive, "pvp"));

        await _operations.Received(1).IsActiveAsync("terraria-pvp", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AmbiguousName_AsksUser_AndDoesNotExecute()
    {
        // "terraria" matches two instances by game type / substring.
        var result = await Create().ExecuteAsync(Call(LlmTools.IsServerActive, "terraria"));

        result.Should().Contain("Ambiguous")
            .And.Contain("terraria-pvp")
            .And.Contain("terraria-creative");
        await _operations.DidNotReceive().IsActiveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnknownName_ReturnsMiss_WithKnownList()
    {
        var result = await Create().ExecuteAsync(Call(LlmTools.IsServerActive, "doesnotexist"));

        result.Should().Contain("no instance named").And.Contain("minecraft");
        await _operations.DidNotReceive().IsActiveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnknownTool_IsRefused()
    {
        var result = await Create().ExecuteAsync(
            new LlmToolCall("delete_everything", new Dictionary<string, string?>()));

        result.Should().Contain("not a known tool");
    }

    [Fact]
    public async Task UninstallServer_StagesConfirmation_AndDoesNotExecute()
    {
        string result;
        using (_confirmations.BeginTurn())
        {
            result = await Create().ExecuteAsync(Call(LlmTools.UninstallServer, "minecraft"));

            _confirmations.Staged.Should().ContainSingle()
                .Which.Should().BeEquivalentTo(new PendingConfirmation(ConfirmationKind.Uninstall, "minecraft"));
        }

        result.Should().Contain("Staged").And.Contain("confirm");
    }

    [Fact]
    public async Task UninstallServer_AmbiguousTarget_DoesNotStage()
    {
        string result;
        using (_confirmations.BeginTurn())
        {
            result = await Create().ExecuteAsync(Call(LlmTools.UninstallServer, "terraria"));
            _confirmations.Staged.Should().BeEmpty();
        }

        result.Should().Contain("Ambiguous");
    }

    [Fact]
    public async Task InstallServer_ResolvesBlueprint_AndStagesConfirmation()
    {
        using (_confirmations.BeginTurn())
        {
            var result = await Create().ExecuteAsync(InstallCall("valheim", "my-valheim"));

            result.Should().Contain("Staged");
            _confirmations.Staged.Should().ContainSingle()
                .Which.Should().BeEquivalentTo(
                    new PendingConfirmation(ConfirmationKind.Install, "valheim", "my-valheim"));
        }
    }

    [Fact]
    public async Task InstallServer_NameCollision_DoesNotStage()
    {
        using (_confirmations.BeginTurn())
        {
            var result = await Create().ExecuteAsync(InstallCall("valheim", "minecraft"));

            result.Should().Contain("already exists");
            _confirmations.Staged.Should().BeEmpty();
        }
    }
}
