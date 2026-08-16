using FluentAssertions;

using TheKrystalShip.KGSM.Events;
using TheKrystalShip.KGSM.Extensions;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

/// <summary>
/// The guard that keeps this assembly out of the host's real journal is itself guarded.
/// </summary>
/// <remarks>
/// ⚠ Without this, <see cref="JournalIsolation"/> is a module initializer nobody exercises: delete it,
/// rename the environment variable it sets, or change how the writer resolves its root, and every test
/// run quietly resumes writing hundreds of lines into <c>/var/lib/kgsm-assistant/events</c> — with
/// nothing failing, which is how it went unnoticed the first time.
/// </remarks>
public sealed class JournalIsolationTests
{
    [Fact]
    public void ThisAssembly_WritesItsJournalToATempRoot()
    {
        Environment.GetEnvironmentVariable(JournalServiceCollectionExtensions.StateRootVariable)
            .Should().Be(JournalIsolation.StateRoot)
            .And.NotBe(JournalLayout.DefaultStateRoot);
    }

    /// <summary>
    /// The redirected root is where the writer would actually land.
    /// </summary>
    /// <remarks>
    /// Asserting the variable alone would pass if the writer stopped reading it. This asks the layout
    /// the same question the writer asks, so the two cannot drift apart silently.
    /// </remarks>
    [Fact]
    public void TheRedirectedRoot_IsWhereTheWriterWouldLand()
    {
        string directory = JournalLayout.DirectoryFor(AssistantJournal.Producer, JournalIsolation.StateRoot);

        directory.Should().StartWith(JournalIsolation.StateRoot);
        directory.Should().NotStartWith(JournalLayout.DefaultStateRoot + "/kgsm-assistant");
    }
}
