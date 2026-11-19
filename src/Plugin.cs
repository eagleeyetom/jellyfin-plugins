using System;
using System.Collections.Generic;
using Jellyfin.Plugin.PositionedSubtitles.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.PositionedSubtitles;

/// <summary>
/// Converts embedded SRT tracks that carry {\anN} positioning into external ASS files,
/// because the Jellyfin web client (and Tizen) ignores positioning in SRT.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Positioned Subtitles";

    public override Guid Id => Guid.Parse("2f8a634e-fa86-4c06-bfa1-a50c1c53908a");

    public override string Description =>
        "Converts embedded SRT subtitles with {\\anN} positioning to external styled ASS files and keeps the subtitle cache indices consistent.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
            }
        };
    }
}
