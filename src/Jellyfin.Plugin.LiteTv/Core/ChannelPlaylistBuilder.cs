using System.Collections.Concurrent;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.LiteTv.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiteTv.Core;

/// <summary>
/// Expands a channel's sources (movies, series, collections) into the flat ordered
/// queue the <see cref="ScheduleResolver"/> operates on. Results are cached briefly
/// so EPG polling stays cheap; deleted items and items without a runtime are skipped.
/// </summary>
public class ChannelPlaylistBuilder
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ChannelPlaylistBuilder> _logger;
    private readonly ConcurrentDictionary<Guid, (DateTime BuiltUtc, IReadOnlyList<ScheduledEntry> Entries)> _cache = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelPlaylistBuilder"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="logger">The logger.</param>
    public ChannelPlaylistBuilder(ILibraryManager libraryManager, ILogger<ChannelPlaylistBuilder> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;

        // Channel edits should be visible immediately, not after the cache TTL.
        if (Plugin.Instance is not null)
        {
            Plugin.Instance.ConfigurationChanged += (_, _) => Invalidate();
        }
    }

    /// <summary>
    /// Gets the expanded queue for the given channel, using a short-lived cache.
    /// </summary>
    /// <param name="channel">The channel definition.</param>
    /// <returns>The ordered playable entries; may be empty.</returns>
    public IReadOnlyList<ScheduledEntry> GetEntries(TvChannel channel)
    {
        if (_cache.TryGetValue(channel.Id, out var cached) && DateTime.UtcNow - cached.BuiltUtc < CacheTtl)
        {
            return cached.Entries;
        }

        var entries = Build(channel);
        _cache[channel.Id] = (DateTime.UtcNow, entries);
        return entries;
    }

    /// <summary>
    /// Drops all cached queues (called when the plugin configuration changes).
    /// </summary>
    public void Invalidate()
    {
        _cache.Clear();
    }

    private IReadOnlyList<ScheduledEntry> Build(TvChannel channel)
    {
        // Expand each source into its own ordered list ("stream"). With the default
        // block size the streams are simply concatenated (each source played in full);
        // with a positive block size they are interleaved round-robin (see below).
        var streams = new List<List<ScheduledEntry>>();
        foreach (var source in channel.Sources)
        {
            var item = _libraryManager.GetItemById(source.ItemId);
            if (item is null)
            {
                _logger.LogWarning("LiteTV channel {Channel}: source item {ItemId} no longer exists; skipping.", channel.Name, source.ItemId);
                continue;
            }

            var stream = new List<ScheduledEntry>();
            switch (item)
            {
                case Series series:
                    AddSeries(stream, series);
                    break;
                case BoxSet boxSet:
                    AddCollection(stream, boxSet);
                    break;
                default:
                    AddIfPlayable(stream, item);
                    break;
            }

            if (stream.Count > 0)
            {
                streams.Add(stream);
            }
        }

        return Interleave(streams, channel.EpisodesPerBlock);
    }

    /// <summary>
    /// Merges the per-source streams. A non-positive block size concatenates them in
    /// order (each source in full). A positive block size rotates through the sources
    /// taking up to that many items from each per round, so multiple series air in
    /// alternating blocks, e.g. block 2 over [S1, S2] gives S1E1, S1E2, S2E1, S2E2,
    /// S1E3, S1E4, ... Uneven streams simply drop out once exhausted.
    /// </summary>
    private static IReadOnlyList<ScheduledEntry> Interleave(List<List<ScheduledEntry>> streams, int blockSize)
    {
        if (blockSize <= 0 || streams.Count <= 1)
        {
            return streams.SelectMany(s => s).ToList();
        }

        var result = new List<ScheduledEntry>();
        var cursors = new int[streams.Count];
        bool progressed;
        do
        {
            progressed = false;
            for (var i = 0; i < streams.Count; i++)
            {
                var stream = streams[i];
                for (var k = 0; k < blockSize && cursors[i] < stream.Count; k++)
                {
                    result.Add(stream[cursors[i]++]);
                    progressed = true;
                }
            }
        }
        while (progressed);

        return result;
    }

    private void AddSeries(List<ScheduledEntry> entries, Series series)
    {
        var episodes = _libraryManager.GetItemList(new InternalItemsQuery
        {
            AncestorIds = new[] { series.Id },
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            Recursive = true
        })
            .OfType<Episode>()
            .Where(e => (e.ParentIndexNumber ?? 0) > 0) // skip specials in v1
            .OrderBy(e => e.ParentIndexNumber ?? 0)
            .ThenBy(e => e.IndexNumber ?? 0);

        foreach (var episode in episodes)
        {
            AddIfPlayable(entries, episode, series);
        }
    }

    private void AddCollection(List<ScheduledEntry> entries, BoxSet boxSet)
    {
        var children = boxSet.GetLinkedChildren()
            .OrderBy(c => c.PremiereDate ?? DateTime.MaxValue)
            .ThenBy(c => c.SortName, StringComparer.OrdinalIgnoreCase);

        foreach (var child in children)
        {
            if (child is Series series)
            {
                AddSeries(entries, series);
            }
            else
            {
                AddIfPlayable(entries, child);
            }
        }
    }

    private void AddIfPlayable(List<ScheduledEntry> entries, BaseItem item, Series? series = null)
    {
        var runtime = item.RunTimeTicks ?? 0;
        if (runtime <= 0)
        {
            _logger.LogDebug("LiteTV: item {Name} has no runtime; skipping.", item.Name);
            return;
        }

        var seriesForItem = series ?? (item as Episode)?.Series;
        entries.Add(new ScheduledEntry(
            item.Id,
            item.Name ?? string.Empty,
            seriesForItem?.Name,
            seriesForItem?.Id,
            runtime));
    }
}
