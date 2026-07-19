namespace Jellyfin.Plugin.LiteTv.Core;

/// <summary>
/// Pure schedule math for continuous-loop channels. Deliberately free of Jellyfin
/// dependencies so it is fully unit-testable: given the expanded queue, the anchor
/// and the clock, everything else is determined.
/// </summary>
public static class ScheduleResolver
{
    /// <summary>
    /// Resolves what is on air right now.
    /// </summary>
    /// <param name="entries">The expanded queue; entries with non-positive runtime are ignored.</param>
    /// <param name="anchorUtc">The schedule zero point (UTC).</param>
    /// <param name="nowUtc">The current time (UTC).</param>
    /// <param name="upcomingCount">How many upcoming entries to include.</param>
    /// <returns>The resolved position, or null when the queue has no playable runtime.</returns>
    public static ScheduleNow? Resolve(
        IReadOnlyList<ScheduledEntry> entries,
        DateTime anchorUtc,
        DateTime nowUtc,
        int upcomingCount = 0)
    {
        var playable = new List<ScheduledEntry>();
        long totalTicks = 0;
        foreach (var entry in entries)
        {
            if (entry.RuntimeTicks > 0)
            {
                playable.Add(entry);
                totalTicks += entry.RuntimeTicks;
            }
        }

        if (playable.Count == 0 || totalTicks <= 0)
        {
            return null;
        }

        // Loop position; C# % keeps the sign of the dividend, so normalize for
        // anchors in the future.
        var elapsed = (nowUtc - anchorUtc).Ticks % totalTicks;
        if (elapsed < 0)
        {
            elapsed += totalTicks;
        }

        var index = 0;
        while (elapsed >= playable[index].RuntimeTicks)
        {
            elapsed -= playable[index].RuntimeTicks;
            index++;
        }

        var current = playable[index];
        var startedUtc = nowUtc - TimeSpan.FromTicks(elapsed);

        var upcoming = new List<(ScheduledEntry, DateTime)>();
        var nextStart = startedUtc + TimeSpan.FromTicks(current.RuntimeTicks);
        for (var i = 1; i <= upcomingCount; i++)
        {
            var entry = playable[(index + i) % playable.Count];
            upcoming.Add((entry, nextStart));
            nextStart += TimeSpan.FromTicks(entry.RuntimeTicks);
        }

        return new ScheduleNow(current, index, elapsed, startedUtc, upcoming);
    }
}
