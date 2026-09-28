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
        NotificationDurationMs = 5000;
        StartupDelayMs = 1500;
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
        SuppressHdr10PlusOnLg = true;
        ShowTranscodeReasons = true;
        ShowAudioConversion = true;
        NotifyOnAudioTrackChange = true;
        IgnoreAudioMedia = true;
        CustomTemplate = string.Empty;
        ExcludedLibraryIds = new List<string>();
        ExcludedUserIds = new List<string>();
        EnableFullscreenToastFix = false;
    }

    /// <summary>
    /// Gets or sets a value indicating whether the plugin is globally enabled.
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// Gets or sets the notification duration in milliseconds.
    /// </summary>
    public int NotificationDurationMs { get; set; }

    /// <summary>
    /// Gets or sets the delay after playback starts before the toast is sent, allowing transcode info to register.
    /// </summary>
    public int StartupDelayMs { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to show SDR.
    /// </summary>
    public bool ShowSdr { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to display audio codec info.
    /// </summary>
    public bool ShowAudio { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to use detailed/technical video names.
    /// </summary>
    public bool UseDetailedVideoNames { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether to use detailed/technical audio names.
    /// </summary>
    public bool UseDetailedAudioNames { get; set; } = false;

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

    /// <summary>
    /// Gets or sets a value indicating whether to suppress HDR10+ on non-supporting clients (e.g. LG, Sony).
    /// </summary>
    public bool SuppressHdr10PlusOnLg { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to show transcode reasons when transcoding occurs.
    /// </summary>
    public bool ShowTranscodeReasons { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to show audio conversion (Source -> Target).
    /// </summary>
    public bool ShowAudioConversion { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to notify when user switches audio track during playback.
    /// </summary>
    public bool NotifyOnAudioTrackChange { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to ignore pure audio/music playback.
    /// </summary>
    public bool IgnoreAudioMedia { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether Jellyfin tone mapping should be reported as SDR.
    /// </summary>
    public bool DesktopSdrMode { get; set; } = false;

    /// <summary>
    /// Gets or sets a custom notification template, e.g. "{hdr} • {audio} • {playback} • {bitrate}".
    /// If empty, default formatting is used.
    /// </summary>
    public string CustomTemplate { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the list of excluded library folder IDs.
    /// </summary>
    public List<string> ExcludedLibraryIds { get; set; } = new();

    /// <summary>
    /// Gets or sets the list of excluded user IDs.
    /// </summary>
    public List<string> ExcludedUserIds { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether the server should inject a fullscreen toast fix
    /// script into Jellyfin Web's <c>index.html</c> on every request.
    /// <para>
    /// <b>Purpose:</b> In browser clients, HTML5 fullscreen mode displays the video element in a separate
    /// top layer, hiding Jellyfin's <c>.toastContainer</c> behind the video.
    /// </para>
    /// <para>
    /// <b>Method:</b> When enabled, plugin middleware intercepts <c>/web/index.html</c> HTTP responses
    /// and injects a script that dynamically moves <c>.toastContainer</c> into the active fullscreen
    /// element whenever fullscreen playback begins.
    /// </para>
    /// <para>
    /// <b>Recommendation:</b> Recommended to enable only if a web browser client is your main player and
    /// toasts are hidden in fullscreen. Clients like Samsung Tizen or native TV apps handle toasts natively
    /// and work fine without this option (leave disabled).
    /// </para>
    /// </summary>
    public bool EnableFullscreenToastFix { get; set; } = false;
}
