using Jellyfin.Plugin.LiteTv.Core;
using Xunit;

namespace Jellyfin.Plugin.LiteTv.Tests;

public class ScheduleResolverTests
{
    private static readonly DateTime Anchor = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ScheduledEntry Entry(string name, int minutes, string? series = null)
    {
        return new ScheduledEntry(Guid.NewGuid(), name, series, series is null ? null : Guid.NewGuid(), TimeSpan.FromMinutes(minutes).Ticks);
    }

    [Fact]
    public void Resolve_EmptyQueue_ReturnsNull()
    {
        Assert.Null(ScheduleResolver.Resolve(Array.Empty<ScheduledEntry>(), Anchor, Anchor.AddHours(1)));
    }

    [Fact]
    public void Resolve_OnlyZeroRuntimeEntries_ReturnsNull()
    {
        var entries = new[] { new ScheduledEntry(Guid.NewGuid(), "broken", null, null, 0) };
        Assert.Null(ScheduleResolver.Resolve(entries, Anchor, Anchor.AddHours(1)));
    }

    [Fact]
    public void Resolve_AtAnchor_StartsWithFirstEntryAtOffsetZero()
    {
        var entries = new[] { Entry("A", 30), Entry("B", 60) };
        var now = ScheduleResolver.Resolve(entries, Anchor, Anchor);

        Assert.NotNull(now);
        Assert.Equal("A", now!.Current.Name);
        Assert.Equal(0, now.OffsetTicks);
        Assert.Equal(Anchor, now.CurrentStartedUtc);
    }

    [Fact]
    public void Resolve_MidSecondItem_ReturnsCorrectOffset()
    {
        var entries = new[] { Entry("A", 30), Entry("B", 60) };
        // 45 minutes in: A (30) done, 15 minutes into B.
        var now = ScheduleResolver.Resolve(entries, Anchor, Anchor.AddMinutes(45));

        Assert.Equal("B", now!.Current.Name);
        Assert.Equal(TimeSpan.FromMinutes(15).Ticks, now.OffsetTicks);
        Assert.Equal(Anchor.AddMinutes(30), now.CurrentStartedUtc);
    }

    [Fact]
    public void Resolve_WrapsAroundTheLoop()
    {
        var entries = new[] { Entry("A", 30), Entry("B", 60) };
        // Total loop 90 min; 200 min = 2 full loops + 20 min -> 20 min into A.
        var now = ScheduleResolver.Resolve(entries, Anchor, Anchor.AddMinutes(200));

        Assert.Equal("A", now!.Current.Name);
        Assert.Equal(TimeSpan.FromMinutes(20).Ticks, now.OffsetTicks);
    }

    [Fact]
    public void Resolve_AnchorInFuture_NormalizesNegativeElapsed()
    {
        var entries = new[] { Entry("A", 30), Entry("B", 60) };
        // 10 minutes before the anchor: 80 minutes into the loop -> 50 min into B.
        var now = ScheduleResolver.Resolve(entries, Anchor, Anchor.AddMinutes(-10));

        Assert.Equal("B", now!.Current.Name);
        Assert.Equal(TimeSpan.FromMinutes(50).Ticks, now.OffsetTicks);
    }

    [Fact]
    public void Resolve_SkipsZeroRuntimeEntries()
    {
        var entries = new[]
        {
            Entry("A", 30),
            new ScheduledEntry(Guid.NewGuid(), "broken", null, null, 0),
            Entry("B", 60)
        };
        var now = ScheduleResolver.Resolve(entries, Anchor, Anchor.AddMinutes(31));

        Assert.Equal("B", now!.Current.Name);
        Assert.Equal(TimeSpan.FromMinutes(1).Ticks, now.OffsetTicks);
    }

    [Fact]
    public void Resolve_Upcoming_WrapsAndCarriesStartTimes()
    {
        var entries = new[] { Entry("A", 30), Entry("B", 60), Entry("C", 15) };
        // 100 minutes: loop is 105 -> 10 minutes into C? 30+60=90, so 10 min into C.
        var now = ScheduleResolver.Resolve(entries, Anchor, Anchor.AddMinutes(100), upcomingCount: 3);

        Assert.Equal("C", now!.Current.Name);
        Assert.Equal(3, now.Upcoming.Count);
        Assert.Equal("A", now.Upcoming[0].Entry.Name);
        Assert.Equal(Anchor.AddMinutes(105), now.Upcoming[0].StartUtc);
        Assert.Equal("B", now.Upcoming[1].Entry.Name);
        Assert.Equal(Anchor.AddMinutes(135), now.Upcoming[1].StartUtc);
        Assert.Equal("C", now.Upcoming[2].Entry.Name);
        Assert.Equal(Anchor.AddMinutes(195), now.Upcoming[2].StartUtc);
    }

    [Fact]
    public void Resolve_SingleEntry_LoopsOnItself()
    {
        var entries = new[] { Entry("A", 40) };
        var now = ScheduleResolver.Resolve(entries, Anchor, Anchor.AddMinutes(90), upcomingCount: 2);

        Assert.Equal("A", now!.Current.Name);
        Assert.Equal(TimeSpan.FromMinutes(10).Ticks, now.OffsetTicks);
        Assert.All(now.Upcoming, u => Assert.Equal("A", u.Entry.Name));
    }

    [Fact]
    public void Resolve_ExactBoundary_MovesToNextEntry()
    {
        var entries = new[] { Entry("A", 30), Entry("B", 60) };
        var now = ScheduleResolver.Resolve(entries, Anchor, Anchor.AddMinutes(30));

        Assert.Equal("B", now!.Current.Name);
        Assert.Equal(0, now.OffsetTicks);
    }
}
