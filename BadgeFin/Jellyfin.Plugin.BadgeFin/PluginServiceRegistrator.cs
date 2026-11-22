using Jellyfin.Plugin.BadgeFin.Middleware;
using Jellyfin.Plugin.BadgeFin.Migration;
using Jellyfin.Plugin.BadgeFin.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.BadgeFin;

/// <summary>
/// Registers plugin services with Jellyfin.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IQualityDetectionService, QualityDetectionService>();
        serviceCollection.AddSingleton<IImageOverlayService, ImageOverlayService>();
        serviceCollection.AddSingleton<IImageCacheService, ImageCacheService>();

        // Register middleware via IStartupFilter to intercept image requests for ALL clients
        serviceCollection.AddSingleton<IStartupFilter, BadgeFinStartupFilter>();

        serviceCollection.AddHostedService<LegacyPluginWarningService>();
    }
}
