namespace Jellyfin.Plugin.LiteTv.Core;

/// <summary>
/// One playable slot in a channel's expanded queue.
/// </summary>
/// <param name="ItemId">The library item id.</param>
/// <param name="Name">The display name (episode or movie title).</param>
/// <param name="SeriesName">The series name when the item is an episode, otherwise null.</param>
/// <param name="SeriesId">The series id when the item is an episode, otherwise null.</param>
/// <param name="RuntimeTicks">The item runtime in ticks; always &gt; 0 for scheduled entries.</param>
public sealed record ScheduledEntry(Guid ItemId, string Name, string? SeriesName, Guid? SeriesId, long RuntimeTicks);

/// <summary>
/// The result of resolving a channel schedule against the clock.
/// </summary>
/// <param name="Current">The entry on air right now.</param>
/// <param name="CurrentIndex">The index of <paramref name="Current"/> in the queue.</param>
/// <param name="OffsetTicks">How far into <paramref name="Current"/> the channel is right now.</param>
/// <param name="CurrentStartedUtc">When the current entry started (UTC).</param>
/// <param name="Upcoming">The next entries with their start times (UTC), wrapping around the loop.</param>
public sealed record ScheduleNow(
    ScheduledEntry Current,
    int CurrentIndex,
    long OffsetTicks,
    DateTime CurrentStartedUtc,
    IReadOnlyList<(ScheduledEntry Entry, DateTime StartUtc)> Upcoming);
