using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MetadataNotifier.Configuration;

/// <summary>
/// Configuration for the MetadataNotifier plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the configuration with default values.
    /// </summary>
    public PluginConfiguration()
    {
        IsEnabled = true;
        TargetSamsungOnly = true;
        NotificationDurationMs = 5000;
        ShowSdr = true;
        ShowAudio = true;
        ShowTranscoding = true;
        ShowDirectPlay = true;
        ShowBitrate = true;
        ShowHdr10Plus = true;
        ShowHdr10 = true;
        ShowDolbyVision = true;
        ShowHlg = true;
        SuppressDvOnSamsung = true;
    }

    /// <summary>
    /// Gets or sets a value indicating whether the plugin is globally enabled.
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to target only Samsung / Tizen clients.
    /// </summary>
    public bool TargetSamsungOnly { get; set; }

    /// <summary>
    /// Gets or sets the notification duration in milliseconds.
    /// </summary>
    public int NotificationDurationMs { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to show SDR.
    /// </summary>
    public bool ShowSdr { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to display audio codec info.
    /// </summary>
    public bool ShowAudio { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to display transcoding info.
    /// </summary>
    public bool ShowTranscoding { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to display Direct Play info.
    /// </summary>
    public bool ShowDirectPlay { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to display bitrate info.
    /// </summary>
    public bool ShowBitrate { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to show HDR10+.
    /// </summary>
    public bool ShowHdr10Plus { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to show HDR10.
    /// </summary>
    public bool ShowHdr10 { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to show Dolby Vision.
    /// </summary>
    public bool ShowDolbyVision { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to show HLG.
    /// </summary>
    public bool ShowHlg { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to suppress Dolby Vision on Samsung devices.
    /// </summary>
    public bool SuppressDvOnSamsung { get; set; }
}
