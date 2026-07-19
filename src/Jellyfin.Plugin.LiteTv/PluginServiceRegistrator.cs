using Jellyfin.Plugin.LiteTv.Core;
using Jellyfin.Plugin.LiteTv.Sessions;
using Jellyfin.Plugin.LiteTv.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.LiteTv;

/// <summary>
/// Registers the plugin's services with the server's dependency injection container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ChannelPlaylistBuilder>();

        // Tracks tuned sessions: watch-state snapshot/restore for all of them, and
        // schedule-following pushes for sessions tuned via the PlayOn endpoint.
        serviceCollection.AddSingleton<TunedSessionMonitor>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<TunedSessionMonitor>());

        // Preferred: register the script injection with the File Transformation
        // plugin when installed (same mechanism as Intro Skipper).
        serviceCollection.AddHostedService<FileTransformationRegistration>();

        // Fallback: request-time injection into the web client's index.html; works
        // even when the web directory on disk is read-only. Stands down when File
        // Transformation handles the injection.
        serviceCollection.AddSingleton<IStartupFilter, InjectionStartupFilter>();
    }
}
