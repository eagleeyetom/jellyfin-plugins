using System;
using System.Collections.Generic;
using Jellyfin.Plugin.MetadataNotifier.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.MetadataNotifier;

/// <summary>
/// Main plugin class for Metadata Notifier.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Unique plugin identifier.
    /// </summary>
    public static readonly Guid PluginId = new("b7c3e2f1-4a5d-6e7f-8b9c-1a2b3c4d5e6f");

    /// <summary>
    /// Singleton instance of the plugin.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Initializes a new instance of the plugin.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "Metadata Notifier";

    /// <inheritdoc />
    public override string Description => "Displays on-screen toast notifications with HDR metadata, audio codec, and transcoding info for Samsung TVs and other clients.";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <summary>
    /// Returns the plugin's web pages.
    /// </summary>
    /// <returns>Collection of web pages.</returns>
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "MetadataNotifier",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
            }
        };
    }
}
