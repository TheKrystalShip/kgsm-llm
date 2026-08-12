using FluentAssertions;

using TheKrystalShip.Kgsm.Assistant.Service.Configuration;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

/// <summary>
/// Where the conversation database is opened. It is the assistant's memory and the record the
/// Control Panel reads, so the rule that picks its path is worth pinning: systemd's directory is
/// used when there is one, an operator's own path always wins, and blank stays blank — the CLI
/// shares this options class and keeps its history beside its binary.
/// </summary>
/// <remarks>
/// <c>$STATE_DIRECTORY</c> is process-wide, so these run in a collection of their own — a parallel
/// test reading it while one of these has it set would see the other's value.
/// </remarks>
[Collection(nameof(StatePathsTests))]
[CollectionDefinition(nameof(StatePathsTests), DisableParallelization = true)]
public sealed class StatePathsTests : IDisposable
{
    private const string Variable = "STATE_DIRECTORY";

    private readonly string? _saved = Environment.GetEnvironmentVariable(Variable);

    public void Dispose() => Environment.SetEnvironmentVariable(Variable, _saved);

    private static string? Resolve(string? configured) => StatePaths.Resolve(
        configured, StatePaths.DefaultConversationDbPath, StatePaths.ConversationDbFileName);

    [Fact]
    public void Uses_the_systemd_state_directory_when_the_value_is_the_shipped_default()
    {
        Environment.SetEnvironmentVariable(Variable, "/var/lib/kgsm-assistant");

        Resolve(StatePaths.DefaultConversationDbPath).Should().Be("/var/lib/kgsm-assistant/conversations.db");
    }

    [Fact]
    public void Follows_the_state_directory_wherever_the_unit_puts_it()
    {
        Environment.SetEnvironmentVariable(Variable, "/somewhere/else");

        Resolve(StatePaths.DefaultConversationDbPath).Should().Be(Path.Combine("/somewhere/else", "conversations.db"));
    }

    [Fact]
    public void Takes_the_first_entry_when_the_unit_declares_several_directories()
    {
        Environment.SetEnvironmentVariable(Variable, "/var/lib/kgsm-assistant:/var/lib/other");

        StatePaths.Directory.Should().Be("/var/lib/kgsm-assistant");
    }

    [Fact]
    public void Falls_back_to_the_shipped_location_outside_systemd()
    {
        Environment.SetEnvironmentVariable(Variable, null);

        StatePaths.Directory.Should().Be(StatePaths.DefaultDirectory);
        Resolve(StatePaths.DefaultConversationDbPath).Should().Be(StatePaths.DefaultConversationDbPath);
    }

    [Fact]
    public void A_configured_path_wins_over_the_state_directory()
    {
        Environment.SetEnvironmentVariable(Variable, "/var/lib/kgsm-assistant");

        Resolve("/opt/elsewhere/history.db").Should().Be("/opt/elsewhere/history.db");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Blank_is_left_blank_so_the_library_default_still_applies(string? configured)
    {
        Environment.SetEnvironmentVariable(Variable, "/var/lib/kgsm-assistant");

        Resolve(configured).Should().Be(configured);
    }
}
