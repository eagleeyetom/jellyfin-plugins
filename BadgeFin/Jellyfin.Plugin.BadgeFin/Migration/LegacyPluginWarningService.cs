using MediaBrowser.Common.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BadgeFin.Migration;

/// <summary>
/// Logs a warning at startup when a JellyTag plugin is still active next to BadgeFin.
/// </summary>
public class LegacyPluginWarningService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<LegacyPluginWarningService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LegacyPluginWarningService"/> class.
    /// </summary>
    /// <param name="serviceProvider">The service provider.</param>
    /// <param name="logger">The logger.</param>
    public LegacyPluginWarningService(IServiceProvider serviceProvider, ILogger<LegacyPluginWarningService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Resolved here instead of injected so a missing service can never stop the server from starting.
        try
        {
            var pluginManager = _serviceProvider.GetService<IPluginManager>();
            if (pluginManager is not null && LegacyJellyTag.IsActive(pluginManager.Plugins))
            {
                _logger.LogWarning(
                    "JellyTag is installed and enabled next to BadgeFin, so badges are drawn twice. "
                    + "BadgeFin is the renamed JellyTag fork and has already copied its settings and custom badges: uninstall JellyTag and restart Jellyfin");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not check for an active JellyTag plugin");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
