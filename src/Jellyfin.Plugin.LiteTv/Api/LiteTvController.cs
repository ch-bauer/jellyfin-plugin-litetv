using Jellyfin.Plugin.LiteTv.Core;
using Jellyfin.Plugin.LiteTv.Sessions;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LiteTv.Api;

/// <summary>
/// API endpoints for LiteTV channels. All endpoints require an authenticated user:
/// the responses expose library content.
/// </summary>
[ApiController]
[Route("LiteTv")]
[Authorize]
public class LiteTvController : ControllerBase
{
    private readonly ChannelPlaylistBuilder _playlistBuilder;
    private readonly TunedSessionMonitor _sessionMonitor;
    private readonly ISessionManager _sessionManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiteTvController"/> class.
    /// </summary>
    /// <param name="playlistBuilder">The channel playlist builder.</param>
    /// <param name="sessionMonitor">The tuned session monitor.</param>
    /// <param name="sessionManager">The session manager.</param>
    public LiteTvController(
        ChannelPlaylistBuilder playlistBuilder,
        TunedSessionMonitor sessionMonitor,
        ISessionManager sessionManager)
    {
        _playlistBuilder = playlistBuilder;
        _sessionMonitor = sessionMonitor;
        _sessionManager = sessionManager;
    }

    /// <summary>
    /// Gets the UI options and all enabled channels with what is on air right now.
    /// </summary>
    /// <returns>The guide payload.</returns>
    [HttpGet("Channels")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<GuideDto> GetChannels()
    {
        var config = Plugin.Instance?.Configuration;
        var result = new GuideDto
        {
            EnableWebUi = config?.EnableWebUi ?? false,
            ShowHomeRow = config?.ShowHomeRow ?? false
        };

        foreach (var channel in config?.Channels ?? new())
        {
            if (!channel.Enabled)
            {
                continue;
            }

            var now = ScheduleResolver.Resolve(_playlistBuilder.GetEntries(channel), channel.AnchorUtc, DateTime.UtcNow, upcomingCount: 1);
            result.Channels.Add(new ChannelSummaryDto
            {
                Id = channel.Id,
                Name = channel.Name,
                Now = now is null ? null : ToProgram(now.Current, now.CurrentStartedUtc),
                Next = now?.Upcoming.Count > 0 ? ToProgram(now.Upcoming[0].Entry, now.Upcoming[0].StartUtc) : null
            });
        }

        return result;
    }

    /// <summary>
    /// Gets the precise on-air position and upcoming programs for one channel.
    /// </summary>
    /// <param name="channelId">The channel id.</param>
    /// <param name="upcoming">How many upcoming programs to include.</param>
    /// <returns>The EPG payload, or 404 when the channel is unknown, disabled or empty.</returns>
    [HttpGet("Channels/{channelId}/Now")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<ChannelNowDto> GetNow([FromRoute] Guid channelId, [FromQuery] int upcoming = 5)
    {
        var channel = Plugin.Instance?.Configuration.Channels
            .FirstOrDefault(c => c.Id == channelId && c.Enabled);
        if (channel is null)
        {
            return NotFound();
        }

        var now = ScheduleResolver.Resolve(
            _playlistBuilder.GetEntries(channel),
            channel.AnchorUtc,
            DateTime.UtcNow,
            Math.Clamp(upcoming, 0, 20));
        if (now is null)
        {
            return NotFound();
        }

        return new ChannelNowDto
        {
            ChannelId = channel.Id,
            ChannelName = channel.Name,
            Current = ToProgram(now.Current, now.CurrentStartedUtc),
            OffsetTicks = now.OffsetTicks,
            ServerTimeUtc = DateTime.UtcNow,
            Upcoming = now.Upcoming.Select(u => ToProgram(u.Entry, u.StartUtc)).ToList()
        };
    }

    /// <summary>
    /// Marks a session as tuned to a channel. The injected web script calls this when it
    /// starts channel playback itself; the server then keeps the account's watch state
    /// clean but does not push follow-up items (the script handles those).
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="channelId">The channel id.</param>
    /// <returns>No content.</returns>
    [HttpPost("Tuned")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult Tune([FromQuery] string sessionId, [FromQuery] Guid channelId)
    {
        _sessionMonitor.Tune(sessionId, channelId, followSchedule: false);
        return NoContent();
    }

    /// <summary>
    /// Removes the tuned mark from a session (the viewer left the channel).
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <returns>No content.</returns>
    [HttpDelete("Tuned")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult Untune([FromQuery] string sessionId)
    {
        _sessionMonitor.Untune(sessionId);
        return NoContent();
    }

    /// <summary>
    /// Tunes another session (e.g. a native TV client) to a channel: sends it a play
    /// command at the live position and lets the server push follow-up items so the
    /// schedule keeps running without the injected script.
    /// </summary>
    /// <param name="channelId">The channel id.</param>
    /// <param name="sessionId">The target session id.</param>
    /// <returns>No content, or 404 when the channel is unknown, disabled or empty.</returns>
    [HttpPost("Channels/{channelId}/PlayOn/{sessionId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> PlayOn([FromRoute] Guid channelId, [FromRoute] string sessionId)
    {
        var channel = Plugin.Instance?.Configuration.Channels
            .FirstOrDefault(c => c.Id == channelId && c.Enabled);
        if (channel is null)
        {
            return NotFound();
        }

        var now = ScheduleResolver.Resolve(_playlistBuilder.GetEntries(channel), channel.AnchorUtc, DateTime.UtcNow);
        if (now is null)
        {
            return NotFound();
        }

        _sessionMonitor.Tune(sessionId, channelId, followSchedule: true);
        await _sessionManager.SendPlayCommand(
            sessionId,
            sessionId,
            new PlayRequest
            {
                ItemIds = new[] { now.Current.ItemId },
                StartPositionTicks = now.OffsetTicks,
                PlayCommand = PlayCommand.PlayNow
            },
            HttpContext.RequestAborted).ConfigureAwait(false);
        return NoContent();
    }

    private static ProgramDto ToProgram(ScheduledEntry entry, DateTime startUtc)
    {
        return new ProgramDto
        {
            ItemId = entry.ItemId,
            Name = entry.Name,
            SeriesName = entry.SeriesName,
            SeriesId = entry.SeriesId,
            StartUtc = startUtc,
            EndUtc = startUtc + TimeSpan.FromTicks(entry.RuntimeTicks),
            RuntimeTicks = entry.RuntimeTicks
        };
    }
}

/// <summary>
/// The channel guide payload.
/// </summary>
public class GuideDto
{
    /// <summary>Gets or sets a value indicating whether the injected web UI is enabled.</summary>
    public bool EnableWebUi { get; set; }

    /// <summary>Gets or sets a value indicating whether the home row is enabled.</summary>
    public bool ShowHomeRow { get; set; }

    /// <summary>Gets the enabled channels.</summary>
    public List<ChannelSummaryDto> Channels { get; } = new();
}

/// <summary>
/// One channel with its on-air program.
/// </summary>
public class ChannelSummaryDto
{
    /// <summary>Gets or sets the channel id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the channel name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the program on air right now.</summary>
    public ProgramDto? Now { get; set; }

    /// <summary>Gets or sets the following program.</summary>
    public ProgramDto? Next { get; set; }
}

/// <summary>
/// The precise on-air position for one channel.
/// </summary>
public class ChannelNowDto
{
    /// <summary>Gets or sets the channel id.</summary>
    public Guid ChannelId { get; set; }

    /// <summary>Gets or sets the channel name.</summary>
    public string ChannelName { get; set; } = string.Empty;

    /// <summary>Gets or sets the program on air.</summary>
    public ProgramDto Current { get; set; } = new();

    /// <summary>Gets or sets how far into the current program the channel is.</summary>
    public long OffsetTicks { get; set; }

    /// <summary>Gets or sets the server time the offset was computed at (UTC), so the
    /// client can correct for its own request latency and clock skew.</summary>
    public DateTime ServerTimeUtc { get; set; }

    /// <summary>Gets or sets the upcoming programs.</summary>
    public List<ProgramDto> Upcoming { get; set; } = new();
}

/// <summary>
/// One scheduled program.
/// </summary>
public class ProgramDto
{
    /// <summary>Gets or sets the library item id.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the title.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the series name for episodes.</summary>
    public string? SeriesName { get; set; }

    /// <summary>Gets or sets the series id for episodes.</summary>
    public Guid? SeriesId { get; set; }

    /// <summary>Gets or sets the start time (UTC).</summary>
    public DateTime StartUtc { get; set; }

    /// <summary>Gets or sets the end time (UTC).</summary>
    public DateTime EndUtc { get; set; }

    /// <summary>Gets or sets the runtime in ticks.</summary>
    public long RuntimeTicks { get; set; }
}
