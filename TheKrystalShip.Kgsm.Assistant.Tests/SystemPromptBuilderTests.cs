using FluentAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Ports;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Tests;

/// <summary>
/// The prompt text lives in the library (<see cref="KgsmAssistantPrompts"/>) so every
/// host shares it without copy-pasting config. These verify the builder uses those
/// defaults when no Llm:* config is present, and still honors a host override.
/// </summary>
public class SystemPromptBuilderTests
{
    private readonly IServerInventory _inventory = Substitute.For<IServerInventory>();

    public SystemPromptBuilderTests()
    {
        _inventory.GetInstancesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>()));
        _inventory.GetBlueprintNamesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<string>>(Array.Empty<string>()));
    }

    private SystemPromptBuilder Build(params (string key, string value)[] config)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config.ToDictionary(c => c.key, c => (string?)c.value))
            .Build();
        return new SystemPromptBuilder(_inventory, NullLogger<SystemPromptBuilder>.Instance, configuration);
    }

    [Fact]
    public async Task NoConfig_UsesLibDefaultPreambleAndDeniedText()
    {
        var prompt = await Build().BuildAsync(canPerformActions: false);

        prompt.Should().StartWith(KgsmAssistantPrompts.Preamble);
        prompt.Should().Contain(KgsmAssistantPrompts.ActionsDenied);
        prompt.Should().NotContain(KgsmAssistantPrompts.ActionsAllowed);
    }

    [Fact]
    public async Task NoConfig_AuthorizedCaller_UsesLibDefaultAllowedText()
    {
        var prompt = await Build().BuildAsync(canPerformActions: true);

        prompt.Should().Contain(KgsmAssistantPrompts.ActionsAllowed);
        prompt.Should().NotContain(KgsmAssistantPrompts.ActionsDenied);
    }

    [Fact]
    public async Task ConfigOverride_TakesPrecedenceOverLibDefault()
    {
        var prompt = await Build(
            ("Llm:Preamble", "CUSTOM PREAMBLE"),
            ("Llm:ActionsDenied", "CUSTOM DENIED")).BuildAsync(canPerformActions: false);

        prompt.Should().StartWith("CUSTOM PREAMBLE");
        prompt.Should().Contain("CUSTOM DENIED");
        prompt.Should().NotContain(KgsmAssistantPrompts.Preamble);
    }
}
