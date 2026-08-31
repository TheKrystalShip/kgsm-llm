using FluentAssertions;

using TheKrystalShip.Kgsm.Assistant.Service.Cluster;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

/// <summary>
/// Holding what the fleet said for a moment, and holding it per person.
/// </summary>
/// <remarks>
/// The window exists because one turn reads the fleet several times; it is per person because who
/// asked decides which nodes answered. Both halves are load-bearing and neither is visible from a
/// single read, which is why they are pinned here rather than left to be noticed.
/// </remarks>
public sealed class FleetSnapshotTests
{
    [Fact]
    public async Task A_second_read_by_the_same_person_does_not_ask_again()
    {
        var snapshot = new FleetSnapshot<int>(TimeSpan.FromMinutes(5));
        int asked = 0;

        await snapshot.ReadAsync("web:someone", () => Task.FromResult(++asked));
        int second = await snapshot.ReadAsync("web:someone", () => Task.FromResult(++asked));

        second.Should().Be(1);
        asked.Should().Be(1);
    }

    /// <summary>
    /// A node that holds no account for somebody refuses them, and is reported to them as unreached.
    /// Handing that answer to the next person would show them a machine they can in fact reach.
    /// </summary>
    [Fact]
    public async Task One_person_never_reads_what_another_was_told()
    {
        var snapshot = new FleetSnapshot<string>(TimeSpan.FromMinutes(5));

        await snapshot.ReadAsync("web:first", () => Task.FromResult("first"));
        string second = await snapshot.ReadAsync("web:second", () => Task.FromResult("second"));

        second.Should().Be("second");
    }

    [Fact]
    public async Task An_expired_answer_is_asked_for_again()
    {
        var snapshot = new FleetSnapshot<int>(TimeSpan.Zero);
        int asked = 0;

        await snapshot.ReadAsync("web:someone", () => Task.FromResult(++asked));
        await snapshot.ReadAsync("web:someone", () => Task.FromResult(++asked));

        asked.Should().Be(2);
    }

    [Fact]
    public async Task Forgetting_asks_every_node_again()
    {
        var snapshot = new FleetSnapshot<int>(TimeSpan.FromMinutes(5));
        int asked = 0;

        await snapshot.ReadAsync("web:someone", () => Task.FromResult(++asked));
        snapshot.Clear();
        await snapshot.ReadAsync("web:someone", () => Task.FromResult(++asked));

        asked.Should().Be(2);
    }
}
