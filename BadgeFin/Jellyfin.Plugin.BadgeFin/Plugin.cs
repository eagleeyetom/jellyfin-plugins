using System;
using System.Collections.Generic;
using Jellyfin.Plugin.BadgeFin.Configuration;
using Jellyfin.Plugin.BadgeFin.Migration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BadgeFin;

/// <summary>
/// BadgeFin plugin - Adds quality badges to media posters.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Gets the plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{Plugin}"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        // Must run before Configuration is first read, or BadgeFin would create default settings.
        MigrateFromJellyTag(applicationPaths, logger);

        // Run legacy migration once at startup
        Configuration.EnsureValid();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Every path that replaces the configuration (dashboard save, import, reset) goes
    /// through here, so the image pipeline never sees null sections or lists.
    /// </remarks>
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ((PluginConfiguration)configuration).EnsureValid();
        base.UpdateConfiguration(configuration);
    }

    private void MigrateFromJellyTag(IApplicationPaths applicationPaths, ILogger<Plugin> logger)
    {
        try
        {
            var result = LegacyJellyTag.Migrate(
                applicationPaths.PluginConfigurationsPath,
                applicationPaths.PluginsPath,
                ConfigurationFilePath,
                DataFolderPath);

            if (result.ConfigurationCopied || result.CustomBadgesCopied > 0)
            {
                logger.LogInformation(
                    "Copied JellyTag settings to BadgeFin (settings: {ConfigurationCopied}, custom badges: {CustomBadgesCopied})",
                    result.ConfigurationCopied,
                    result.CustomBadgesCopied);
            }
        }
        catch (Exception ex)
        {
            // Never block startup: BadgeFin then simply starts with default settings.
            logger.LogWarning(ex, "Could not copy JellyTag settings to BadgeFin");
        }
    }

    /// <inheritdoc />
    public override string Name => "BadgeFin";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("7354bd14-00d2-410e-9086-4f2b0e1a300d");

    /// <inheritdoc />
    public override string Description => "Adds quality badges (4K, 1080p, etc.) to media posters and thumbnails.";

    /// <summary>
    /// Gets the cache folder path for storing processed images.
    /// </summary>
    public string CacheFolderPath => Path.Combine(DataFolderPath, "cache");

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
                EnableInMainMenu = true,
                MenuSection = "Extensions",
                MenuIcon = "style"
            }
        };
    }
}
