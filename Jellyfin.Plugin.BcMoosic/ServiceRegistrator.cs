using Jellyfin.Plugin.BcMoosic.Bandcamp;
using Jellyfin.Plugin.BcMoosic.Download;
using Jellyfin.Plugin.BcMoosic.Organization;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.BcMoosic;

/// <summary>
/// Registers bcMoosic services with Jellyfin's dependency injection container.
/// </summary>
public class ServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost serverApplicationHost)
    {
        // Bandcamp HTTP client — singleton, holds cookie state
        serviceCollection.AddSingleton<BandcampClient>();

        // Download queue and job tracker
        serviceCollection.AddSingleton<DownloadManager>();

        // File organiser (reads audio tags, places files in <Artist>/<Album>/)
        serviceCollection.AddSingleton<TrackOrganizer>();

        // Background worker that drains the download queue
        serviceCollection.AddHostedService<DownloadWorker>();

        // Note: Jellyfin automatically discovers plugin controllers from loaded assemblies.
        // Do NOT call AddControllers() here — it corrupts Jellyfin's MVC builder.
    }
}
