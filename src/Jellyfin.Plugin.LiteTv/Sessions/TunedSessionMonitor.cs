using System.Collections.Concurrent;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.LiteTv.Configuration;
using Jellyfin.Plugin.LiteTv.Core;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiteTv.Sessions;

/// <summary>
/// Tracks sessions that are tuned to a LiteTV channel and keeps channel viewing from
/// leaving traces on the account: for every channel item played in a tuned session the
/// user's item data (resume position, played flag, play count) is snapshotted at playback
/// start and restored at playback stop, so Continue Watching, Next Up and watched state
/// stay untouched.
/// For sessions tuned via the PlayOn endpoint (native clients without the injected
/// script) it additionally pushes the next scheduled item when an item plays to the end.
/// </summary>
public class TunedSessionMonitor : IHostedService
{
    private static readonly TimeSpan TunedSessionLifetime = TimeSpan.FromHours(8);

    private readonly ISessionManager _sessionManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ChannelPlaylistBuilder _playlistBuilder;
    private readonly ILogger<TunedSessionMonitor> _logger;

    private readonly ConcurrentDictionary<string, TunedSession> _tuned = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="TunedSessionMonitor"/> class.
    /// </summary>
    /// <param name="sessionManager">The session manager.</param>
    /// <param name="userDataManager">The user data manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="playlistBuilder">The channel playlist builder.</param>
    /// <param name="logger">The logger.</param>
    public TunedSessionMonitor(
        ISessionManager sessionManager,
        IUserDataManager userDataManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        ChannelPlaylistBuilder playlistBuilder,
        ILogger<TunedSessionMonitor> logger)
    {
        _sessionManager = sessionManager;
        _userDataManager = userDataManager;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _playlistBuilder = playlistBuilder;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Marks a session as tuned to a channel.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="channelId">The channel id.</param>
    /// <param name="followSchedule">Whether the server should push the next scheduled item
    /// when an item finishes (native clients without the injected script).</param>
    public void Tune(string sessionId, Guid channelId, bool followSchedule)
    {
        _tuned[sessionId] = new TunedSession(channelId, followSchedule);
        Prune();
    }

    /// <summary>
    /// Removes the tuned mark from a session.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    public void Untune(string sessionId)
    {
        _tuned.TryRemove(sessionId, out _);
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        var item = e.Item;
        if (e.Session is null || item is null || !_tuned.TryGetValue(e.Session.Id, out var tuned))
        {
            return;
        }

        tuned.Touch();
        foreach (var user in e.Users)
        {
            var key = (user.Id, item.Id);
            if (tuned.Snapshots.ContainsKey(key))
            {
                continue; // keep the oldest snapshot if the item restarts
            }

            try
            {
                var data = _userDataManager.GetUserData(user, item);
                if (data is null)
                {
                    continue;
                }

                tuned.Snapshots[key] = new UserDataSnapshot(
                    data.PlaybackPositionTicks,
                    data.Played,
                    data.PlayCount,
                    data.LastPlayedDate);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LiteTV: could not snapshot user data for {Item}.", item.Name);
            }
        }
    }

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        var item = e.Item;
        if (e.Session is null || item is null || !_tuned.TryGetValue(e.Session.Id, out var tuned))
        {
            return;
        }

        // Every progress report the server just persisted put the item into
        // Continue Watching; revert it right away so channel viewing stays
        // invisible on the account even while it is running.
        tuned.Touch();
        foreach (var user in e.Users)
        {
            if (!tuned.Snapshots.TryGetValue((user.Id, item.Id), out var snapshot))
            {
                continue;
            }

            try
            {
                var data = _userDataManager.GetUserData(user, item);
                if (data is null || (data.PlaybackPositionTicks == snapshot.PlaybackPositionTicks && data.Played == snapshot.Played))
                {
                    continue;
                }

                data.PlaybackPositionTicks = snapshot.PlaybackPositionTicks;
                data.Played = snapshot.Played;
                data.PlayCount = snapshot.PlayCount;
                data.LastPlayedDate = snapshot.LastPlayedDate;
                _userDataManager.SaveUserData(user, item, data, UserDataSaveReason.TogglePlayed, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "LiteTV: could not revert progress for {Item}.", item.Name);
            }
        }
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        var item = e.Item;
        if (e.Session is null || item is null || !_tuned.TryGetValue(e.Session.Id, out var tuned))
        {
            return;
        }

        tuned.Touch();
        foreach (var user in e.Users)
        {
            if (!tuned.Snapshots.TryRemove((user.Id, item.Id), out var snapshot))
            {
                continue;
            }

            // Deferred: the server's own stop-save and any trailing progress report
            // from the client would otherwise overwrite the restored state again,
            // putting the item back into Continue Watching.
            _ = RestoreUserDataAsync(user, item, snapshot);
        }

        if (tuned.FollowSchedule && e.PlayedToCompletion)
        {
            // Native-mode session finished an item: push whatever the schedule says is
            // on now (at this moment that is the follow-up item near offset zero).
            _ = PushCurrentAsync(e.Session.Id, tuned.ChannelId);
        }
        else if (!e.PlayedToCompletion)
        {
            // The user stopped mid-item: treat as switching the channel off.
            Untune(e.Session.Id);
        }
    }

    private async Task RestoreUserDataAsync(User user, MediaBrowser.Controller.Entities.BaseItem item, UserDataSnapshot snapshot)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);

            var data = _userDataManager.GetUserData(user, item);
            if (data is null)
            {
                return;
            }

            data.PlaybackPositionTicks = snapshot.PlaybackPositionTicks;
            data.Played = snapshot.Played;
            data.PlayCount = snapshot.PlayCount;
            data.LastPlayedDate = snapshot.LastPlayedDate;
            _userDataManager.SaveUserData(user, item, data, UserDataSaveReason.TogglePlayed, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LiteTV: could not restore user data for {Item}.", item.Name);
        }
    }

    private async Task PushCurrentAsync(string sessionId, Guid channelId)
    {
        try
        {
            // Give the client a moment to settle after the stop report.
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

            var channel = Plugin.Instance?.Configuration.Channels
                .FirstOrDefault(c => c.Id == channelId && c.Enabled);
            if (channel is null)
            {
                return;
            }

            var now = ScheduleResolver.Resolve(_playlistBuilder.GetEntries(channel), channel.AnchorUtc, DateTime.UtcNow);
            if (now is null)
            {
                return;
            }

            await _sessionManager.SendPlayCommand(
                sessionId,
                sessionId,
                new PlayRequest
                {
                    ItemIds = new[] { now.Current.ItemId },
                    StartPositionTicks = now.OffsetTicks,
                    PlayCommand = PlayCommand.PlayNow
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LiteTV: could not push the next scheduled item to session {SessionId}.", sessionId);
            Untune(sessionId);
        }
    }

    private void Prune()
    {
        var cutoff = DateTime.UtcNow - TunedSessionLifetime;
        foreach (var pair in _tuned)
        {
            if (pair.Value.LastActivityUtc < cutoff)
            {
                _tuned.TryRemove(pair.Key, out _);
            }
        }
    }

    private sealed class TunedSession
    {
        public TunedSession(Guid channelId, bool followSchedule)
        {
            ChannelId = channelId;
            FollowSchedule = followSchedule;
            LastActivityUtc = DateTime.UtcNow;
        }

        public Guid ChannelId { get; }

        public bool FollowSchedule { get; }

        public DateTime LastActivityUtc { get; private set; }

        public ConcurrentDictionary<(Guid UserId, Guid ItemId), UserDataSnapshot> Snapshots { get; } = new();

        public void Touch() => LastActivityUtc = DateTime.UtcNow;
    }

    private sealed record UserDataSnapshot(long PlaybackPositionTicks, bool Played, int PlayCount, DateTime? LastPlayedDate);
}
