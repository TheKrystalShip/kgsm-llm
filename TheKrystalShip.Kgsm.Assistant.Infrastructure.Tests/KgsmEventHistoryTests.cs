using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using TheKrystalShip.Kgsm.Assistant.Audit;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Kgsm;
using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Tests;

/// <summary>
/// Unit-tests the event-history adapter against a stubbed journal reader — what it asks for, what it
/// hands back, and that it fails closed rather than throwing, which the port requires of it.
/// </summary>
public class KgsmEventHistoryTests
{
    /// <summary>A journal reader that records the query it was given and answers with a canned page.</summary>
    private sealed class StubJournal(Func<EventHistoryQuery, EventHistoryPage> respond) : IEventJournalHistory
    {
        public EventHistoryQuery? LastQuery { get; private set; }

        public Task<EventHistoryPage> QueryAsync(EventHistoryQuery query, CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            return Task.FromResult(respond(query));
        }
    }

    private static KgsmEventHistory Create(IEventJournalHistory journal) =>
        new(journal, NullLogger<KgsmEventHistory>.Instance);

    private static EventHistoryEntry Entry(
        string id, string type = "server.started", string? instance = "factorio",
        string? actor = "discord:haru", string? origin = "ui") =>
        new(id, DateTimeOffset.Parse("2026-08-07T10:00:00Z"), type, instance, null, actor, origin, "hotrod", null);

    private static EventHistoryPage Page(params EventHistoryEntry[] events) =>
        new(events, null, null, DateTimeOffset.Parse("2026-07-08T10:00:00Z"), false, true);

    [Fact]
    public async Task GetEventsAsync_MapsJournalEntries_ToAuditEventRows()
    {
        var journal = new StubJournal(_ => Page(
            Entry("evt_2026-08-07_000000000000"),
            Entry("evt_2026-08-07_000000000128", type: "server.stopped")));

        EventHistoryReading reading = await Create(journal).GetEventsAsync(null, null, 50);

        reading.State.Should().Be(AuditReadState.Available);
        reading.Events.Should().HaveCount(2);
        reading.Events[0].Id.Should().Be("evt_2026-08-07_000000000000");
        reading.Events[0].Type.Should().Be("server.started");
        reading.Events[0].Instance.Should().Be("factorio");
        reading.Events[0].Actor.Should().Be("discord:haru");
        reading.Events[0].Origin.Should().Be("ui");
    }

    [Fact]
    public async Task GetEventsAsync_PassesTheScopeAndWindowThrough()
    {
        var journal = new StubJournal(_ => Page());

        await Create(journal).GetEventsAsync("factorio-test", 1_785_000_000_000, 25);

        journal.LastQuery!.Instance.Should().Be("factorio-test");
        journal.LastQuery.SinceMs.Should().Be(1_785_000_000_000);
        journal.LastQuery.Limit.Should().Be(25);
    }

    /// <summary>
    /// A blank scope means fleet-wide, and must reach the reader as "no constraint" rather than as a
    /// filter on the empty string — which would match nothing and read as a quiet host.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetEventsAsync_BlankInstance_IsNoConstraint(string? instance)
    {
        var journal = new StubJournal(_ => Page());

        await Create(journal).GetEventsAsync(instance, null, 50);

        journal.LastQuery!.Instance.Should().BeNull();
    }

    /// <summary>
    /// Nothing matched is a real answer and must stay distinguishable from "I could not look" — the
    /// composers narrate the two differently, and conflating them invents a quiet period.
    /// </summary>
    [Fact]
    public async Task GetEventsAsync_EmptyPage_IsAvailable_NotUnavailable()
    {
        var journal = new StubJournal(_ => Page());

        EventHistoryReading reading = await Create(journal).GetEventsAsync(null, null, 50);

        reading.State.Should().Be(AuditReadState.Available);
        reading.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task GetEventsAsync_UnreadableJournal_IsUnavailable_NeverThrows()
    {
        var journal = new StubJournal(_ => EventHistoryPage.Unreadable);

        EventHistoryReading reading = await Create(journal).GetEventsAsync(null, null, 50);

        reading.State.Should().Be(AuditReadState.JournalUnavailable);
        reading.Events.Should().BeEmpty();
    }

    /// <summary>
    /// The reader promises not to throw for a missing or unreadable journal. The port's promise is
    /// stricter — it must not throw at all — so the adapter absorbs a broken promise rather than
    /// letting one bad read take down a turn.
    /// </summary>
    [Fact]
    public async Task GetEventsAsync_ReaderThrows_IsUnavailable_NeverThrows()
    {
        var journal = new StubJournal(_ => throw new IOException("journal exploded"));

        EventHistoryReading reading = await Create(journal).GetEventsAsync(null, null, 50);

        reading.State.Should().Be(AuditReadState.JournalUnavailable);
        reading.Events.Should().BeEmpty();
    }
}
