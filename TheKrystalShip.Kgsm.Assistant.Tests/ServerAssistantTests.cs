using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// Verifies the kgsm authorization policy that <see cref="ServerAssistant"/>
/// supplies to the (library) agent loop: which tools are offered per caller, the
/// single per-message staging cap (every command is propose-only, §3.5), and the
/// unauthorized-caller refusal. The loop itself is the library's concern (and is
/// tested there); here we capture the <see cref="AgentTurn"/> the assistant builds
/// and exercise its gate directly.
/// </summary>
public class ServerAssistantTests
{
    private const string Conversation = "1:2";

    private readonly ILlmAgent _agent = Substitute.For<ILlmAgent>();
    private readonly ISystemPromptBuilder _prompt = Substitute.For<ISystemPromptBuilder>();
    private readonly IConfirmationContext _confirmations = new ConfirmationContext();
    private readonly ITurnProgress _progress = Substitute.For<ITurnProgress>();
    private readonly IServerInventory _inventory = Substitute.For<IServerInventory>();
    private readonly IServerOperations _operations = Substitute.For<IServerOperations>();
    private readonly IBlueprintAuthoring _blueprintAuthoring = Substitute.For<IBlueprintAuthoring>();

    // Default: search, fetch, AND blueprint authoring are all AVAILABLE, so the offered set is the
    // unfiltered catalog (BeSameAs holds) and the gate's per-message caps are exercisable. Availability
    // tests pass their own.
    private ServerAssistant Create(
        SearchOptions? search = null, FetchOptions? fetch = null, BlueprintAuthoringFlags? blueprint = null)
    {
        _prompt.BuildAsync(Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new BuiltPrompt("system", "deadbeef"));
        return new ServerAssistant(
            _agent, _prompt, _confirmations, _progress, _inventory, _operations, Substitute.For<INetworkInfo>(), Substitute.For<IUpnpInfo>(),
            new NoopToolRelevanceFilter(), new PassthroughPromptOverrides(), _blueprintAuthoring,
            Options.Create(search ?? new SearchOptions { WebEnabled = true }),
            Options.Create(fetch ?? new FetchOptions { Available = true }),
            Options.Create(blueprint ?? new BlueprintAuthoringFlags { Available = true }),
            NullLogger<ServerAssistant>.Instance);
    }

    /// <summary>Runs a turn and returns the AgentTurn the assistant handed to the loop.</summary>
    private async Task<AgentTurn> CaptureTurnAsync(
        bool canPerformActions, SearchOptions? search = null, FetchOptions? fetch = null,
        BlueprintAuthoringFlags? blueprint = null)
    {
        AgentTurn? captured = null;
        _agent.RunAsync(Arg.Do<AgentTurn>(t => captured = t), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AgentRunResult("ok", null)));

        await Create(search, fetch, blueprint).RunAsync(Conversation, "do it", canPerformActions);

        captured.Should().NotBeNull();
        return captured!;
    }

    private static LlmToolCall Call(Tool name) =>
        new(name, new Dictionary<string, string?> { ["instance_name"] = "terraria" });

    [Fact]
    public async Task AuthorizedCaller_IsOfferedAllTools()
    {
        var turn = await CaptureTurnAsync(canPerformActions: true);
        turn.Tools.Should().BeSameAs(LlmTools.All);
    }

    [Fact]
    public async Task UnauthorizedCaller_IsOfferedOnlyReadOnlyTools()
    {
        var turn = await CaptureTurnAsync(canPerformActions: false);
        turn.Tools.Should().BeSameAs(LlmTools.ReadOnly);
    }

    [Fact]
    public async Task UnauthorizedCaller_GateRefusesCommand()
    {
        var turn = await CaptureTurnAsync(canPerformActions: false);

        var decision = turn.Gate!(Call(LlmTools.ServerCommand));

        decision.Allowed.Should().BeFalse();
        decision.RefusalMessage.Should().Contain("permission");
    }

    [Fact]
    public async Task Gate_CapsStagedCommandsAtFivePerMessage()
    {
        var turn = await CaptureTurnAsync(canPerformActions: true);
        var stop = Call(LlmTools.ServerCommand);

        // First five proposed commands are allowed (the dispatcher only STAGES them)...
        for (var i = 0; i < 5; i++)
            turn.Gate!(stop).Allowed.Should().BeTrue($"call {i} should be within the cap");

        // ...the sixth is refused.
        var sixth = turn.Gate!(stop);
        sixth.Allowed.Should().BeFalse();
        sixth.RefusalMessage.Should().Contain("separately");
    }

    [Fact]
    public async Task Gate_OneCap_SpansEveryCommandKind()
    {
        // §3.5: ordinary commands and the formerly-"destructive" ops now share ONE staging
        // cap — a mix of kinds counts together, with no separate budget per tier.
        var turn = await CaptureTurnAsync(canPerformActions: true);

        turn.Gate!(Call(LlmTools.ServerCommand)).Allowed.Should().BeTrue();
        turn.Gate!(Call(LlmTools.UninstallServer)).Allowed.Should().BeTrue();
        turn.Gate!(Call(LlmTools.InstallServer)).Allowed.Should().BeTrue();
        turn.Gate!(Call(LlmTools.SetConfigValue)).Allowed.Should().BeTrue();
        turn.Gate!(Call(LlmTools.WriteFile)).Allowed.Should().BeTrue();

        // Five staged across kinds; the sixth (any kind) is refused.
        turn.Gate!(Call(LlmTools.ServerCommand)).Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task UnauthorizedCaller_GateRefusesWriteFile()
    {
        var turn = await CaptureTurnAsync(canPerformActions: false);

        var decision = turn.Gate!(Call(LlmTools.WriteFile));

        decision.Allowed.Should().BeFalse();
        decision.RefusalMessage.Should().Contain("permission");
    }

    [Fact]
    public async Task Gate_DoesNotCountReadOnlyToolsAgainstTheCap()
    {
        var turn = await CaptureTurnAsync(canPerformActions: true);
        var status = Call(LlmTools.GetStatus);

        // Many read-only calls, all allowed and none consuming the staging budget.
        for (var i = 0; i < 10; i++)
            turn.Gate!(status).Allowed.Should().BeTrue();

        // The staging budget is still fully intact afterwards.
        for (var i = 0; i < 5; i++)
            turn.Gate!(Call(LlmTools.ServerCommand)).Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task Gate_RefusesAuthorizedReadForUnauthorizedCaller()
    {
        var turn = await CaptureTurnAsync(canPerformActions: false);

        var gate = turn.Gate!(Call(LlmTools.ReadFile));

        gate.Allowed.Should().BeFalse();
        gate.RefusalMessage.Should().Contain("permission");
    }

    [Fact]
    public async Task Gate_AllowsAuthorizedReadForAuthorizedCaller_WithoutConsumingStagingCap()
    {
        var turn = await CaptureTurnAsync(canPerformActions: true);
        var view = Call(LlmTools.ReadFile);

        // Many file reads are allowed and none consume the staging budget...
        for (var i = 0; i < 10; i++)
            turn.Gate!(view).Allowed.Should().BeTrue();

        // ...so the full staging budget remains.
        for (var i = 0; i < 5; i++)
            turn.Gate!(Call(LlmTools.ServerCommand)).Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task UnauthorizedCaller_GateRefusesDestructive()
    {
        var turn = await CaptureTurnAsync(canPerformActions: false);

        turn.Gate!(Call(LlmTools.UninstallServer)).Allowed.Should().BeFalse();
        turn.Gate!(Call(LlmTools.InstallServer)).Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task Gate_AllowsSearchForUnauthorizedCaller_ButCapsItPerMessage()
    {
        // search is offered to everyone (read-only tier), so an unauthorized caller may use it — but
        // each call adds a loop iteration (and a web fallback may spend a credit), so the gate caps it
        // per message (the in-turn runaway guard; the per-day web wallet cap is a host-side backstop).
        var turn = await CaptureTurnAsync(canPerformActions: false);
        var search = Call(LlmTools.Search);

        for (var i = 0; i < 5; i++)
            turn.Gate!(search).Allowed.Should().BeTrue($"search {i} is within the per-message cap");

        var sixth = turn.Gate!(search);
        sixth.Allowed.Should().BeFalse();
        sixth.RefusalMessage.Should().Contain("searches per message");
    }

    [Fact]
    public async Task Gate_SearchCap_IsSeparateFromTheStagingCap()
    {
        // The two budgets are independent counters: exhausting searches must not eat into the
        // command-staging budget, and searches never consume staging slots.
        var turn = await CaptureTurnAsync(canPerformActions: true);

        for (var i = 0; i < 5; i++)
            turn.Gate!(Call(LlmTools.Search)).Allowed.Should().BeTrue();
        turn.Gate!(Call(LlmTools.Search)).Allowed.Should().BeFalse(); // search cap hit

        // The full staging budget is still intact.
        for (var i = 0; i < 5; i++)
            turn.Gate!(Call(LlmTools.ServerCommand)).Allowed.Should().BeTrue();
        turn.Gate!(Call(LlmTools.ServerCommand)).Allowed.Should().BeFalse();
    }

    // --- §D7 search availability: the tool is offered iff a source backs it -----------------------

    [Fact]
    public async Task Search_IsOffered_WhenASourceIsAvailable()
    {
        var turn = await CaptureTurnAsync(canPerformActions: false, search: new SearchOptions { WebEnabled = true });
        turn.Tools.Select(t => t.Tool).Should().Contain(LlmTools.Search);
    }

    [Fact]
    public async Task Search_IsOmitted_WhenNoSourceIsAvailable()
    {
        // Neither RAG nor a web provider configured → search is dropped, but the rest of the
        // read-only catalog is still offered.
        var turn = await CaptureTurnAsync(canPerformActions: false, search: new SearchOptions());
        turn.Tools.Select(t => t.Tool).Should().NotContain(LlmTools.Search);
        turn.Tools.Select(t => t.Tool).Should().Contain(LlmTools.GetStatus);
    }

    [Fact]
    public async Task RequestingSearch_WhenUnavailable_IsAnInvalidToolError()
    {
        // A client explicitly requesting `search` on a host where it's unavailable gets the honest
        // invalid-tool error (it's genuinely not in this host's catalog), never a silently-dead tool.
        _agent.RunAsync(Arg.Any<AgentTurn>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AgentRunResult("ok", null)));

        var result = await Create(new SearchOptions()).RunAsync(
            Conversation, "find docs", canPerformActions: false, requestedTools: ["search"]);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Invalid tool");
    }

    // --- fetch_url availability + per-message cap (mirrors the search block above) -----------------

    [Fact]
    public async Task FetchUrl_IsOffered_WhenAvailable()
    {
        var turn = await CaptureTurnAsync(canPerformActions: false, fetch: new FetchOptions { Available = true });
        turn.Tools.Select(t => t.Tool).Should().Contain(LlmTools.FetchUrl);
    }

    [Fact]
    public async Task FetchUrl_IsOmitted_WhenUnavailable()
    {
        var turn = await CaptureTurnAsync(canPerformActions: false, fetch: new FetchOptions { Available = false });
        turn.Tools.Select(t => t.Tool).Should().NotContain(LlmTools.FetchUrl);
        turn.Tools.Select(t => t.Tool).Should().Contain(LlmTools.GetStatus);
    }

    [Fact]
    public async Task RequestingFetchUrl_WhenUnavailable_IsAnInvalidToolError()
    {
        _agent.RunAsync(Arg.Any<AgentTurn>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AgentRunResult("ok", null)));

        var result = await Create(fetch: new FetchOptions { Available = false }).RunAsync(
            Conversation, "read this page", canPerformActions: false, requestedTools: ["fetch_url"]);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Invalid tool");
    }

    [Fact]
    public async Task Gate_AllowsFetchForUnauthorizedCaller_ButCapsItPerMessage()
    {
        // fetch_url is offered to everyone (read-only tier), but each call is a real outbound HTTP
        // request against a model/user-influenced URL and adds a loop iteration — capped per message.
        var turn = await CaptureTurnAsync(canPerformActions: false);
        var fetch = Call(LlmTools.FetchUrl);

        for (var i = 0; i < 5; i++)
            turn.Gate!(fetch).Allowed.Should().BeTrue($"fetch {i} is within the per-message cap");

        var sixth = turn.Gate!(fetch);
        sixth.Allowed.Should().BeFalse();
        sixth.RefusalMessage.Should().Contain("fetches per message");
    }

    [Fact]
    public async Task Gate_FetchCap_IsSeparateFromSearchAndStagingCaps()
    {
        // Three independent counters: exhausting fetches must not eat into the search or
        // command-staging budgets, and vice versa.
        var turn = await CaptureTurnAsync(canPerformActions: true);

        for (var i = 0; i < 5; i++)
            turn.Gate!(Call(LlmTools.FetchUrl)).Allowed.Should().BeTrue();
        turn.Gate!(Call(LlmTools.FetchUrl)).Allowed.Should().BeFalse(); // fetch cap hit

        for (var i = 0; i < 5; i++)
            turn.Gate!(Call(LlmTools.Search)).Allowed.Should().BeTrue();
        turn.Gate!(Call(LlmTools.Search)).Allowed.Should().BeFalse(); // search cap hit, independently

        for (var i = 0; i < 5; i++)
            turn.Gate!(Call(LlmTools.ServerCommand)).Allowed.Should().BeTrue();
        turn.Gate!(Call(LlmTools.ServerCommand)).Allowed.Should().BeFalse(); // staging cap hit, independently
    }

    // --- create_blueprint availability + per-message cap + authorization ----------------------------

    [Fact]
    public async Task CreateBlueprint_IsOffered_WhenAvailable_AndAuthorized()
    {
        var turn = await CaptureTurnAsync(
            canPerformActions: true, blueprint: new BlueprintAuthoringFlags { Available = true });
        turn.Tools.Select(t => t.Tool).Should().Contain(LlmTools.CreateBlueprint);
    }

    [Fact]
    public async Task CreateBlueprint_IsOmitted_WhenUnavailable()
    {
        var turn = await CaptureTurnAsync(
            canPerformActions: true, blueprint: new BlueprintAuthoringFlags { Available = false });
        turn.Tools.Select(t => t.Tool).Should().NotContain(LlmTools.CreateBlueprint);
        turn.Tools.Select(t => t.Tool).Should().Contain(LlmTools.GetStatus);
    }

    [Fact]
    public async Task CreateBlueprint_IsOmitted_ForUnauthorizedCaller_EvenWhenAvailable()
    {
        var turn = await CaptureTurnAsync(
            canPerformActions: false, blueprint: new BlueprintAuthoringFlags { Available = true });
        turn.Tools.Select(t => t.Tool).Should().NotContain(LlmTools.CreateBlueprint);
    }

    [Fact]
    public async Task Gate_RefusesCreateBlueprint_ForUnauthorizedCaller()
    {
        // Defense in depth: even if an unauthorized caller's request somehow reaches the gate (the
        // tool isn't offered per the test above), the gate itself refuses it too.
        var turn = await CaptureTurnAsync(
            canPerformActions: false, blueprint: new BlueprintAuthoringFlags { Available = true });
        var result = turn.Gate!(Call(LlmTools.CreateBlueprint));
        result.Allowed.Should().BeFalse();
        result.RefusalMessage.Should().Contain("permission");
    }

    [Fact]
    public async Task Gate_CapsCreateBlueprint_AtOnePerMessage()
    {
        var turn = await CaptureTurnAsync(canPerformActions: true);
        var call = Call(LlmTools.CreateBlueprint);

        turn.Gate!(call).Allowed.Should().BeTrue();

        var second = turn.Gate!(call);
        second.Allowed.Should().BeFalse();
        second.RefusalMessage.Should().Contain("blueprint");
    }

    // --- P4: the buffered (non-SSE) path never narrates progress ------------------------------------

    [Fact]
    public async Task RunAsync_NeverOpensAProgressScope()
    {
        // The buffered RunAsync path has no channel to write progress frames onto — unlike
        // RunStreamAsync's ProduceStreamAsync, it must never open the ambient sink, so a long tool
        // (create_blueprint) still runs to completion and returns its one terminal card, with no
        // intermediate narration attempted.
        _agent.RunAsync(Arg.Any<AgentTurn>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AgentRunResult("Verified.", null)));

        var result = await Create().RunAsync(Conversation, "make me a rust server", canPerformActions: true);

        result.IsSuccess.Should().BeTrue();
        _progress.DidNotReceiveWithAnyArgs().BeginTurn(default!);
    }
}
