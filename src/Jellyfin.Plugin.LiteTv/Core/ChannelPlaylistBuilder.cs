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
        var entries = new List<ScheduledEntry>();
        foreach (var source in channel.Sources)
        {
            var item = _libraryManager.GetItemById(source.ItemId);
            if (item is null)
            {
                _logger.LogWarning("LiteTV channel {Channel}: source item {ItemId} no longer exists; skipping.", channel.Name, source.ItemId);
                continue;
            }

            switch (item)
            {
                case Series series:
                    AddSeries(entries, series);
                    break;
                case BoxSet boxSet:
                    AddCollection(entries, boxSet);
                    break;
                default:
                    AddIfPlayable(entries, item);
                    break;
            }
        }

        return entries;
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
