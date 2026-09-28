using Jellyfin.Plugin.MetadataNotifier.Middleware;
using Jellyfin.Plugin.MetadataNotifier.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MetadataNotifier;

/// <summary>
/// Registers plugin services in the dependency injection container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHostedService<MetadataNotifierService>();
        serviceCollection.AddSingleton<IStartupFilter, MetadataNotifierStartupFilter>();
    }
}
