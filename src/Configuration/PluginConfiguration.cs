using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.PositionedSubtitles.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Comma separated subtitle languages (ISO 639-2 like "pol" or ISO 639-1 like "pl").</summary>
    public string Languages { get; set; } = "pol";

    /// <summary>Only convert tracks that contain {\anN} tags. When false every matching SRT track is converted.</summary>
    public bool OnlyPositioned { get; set; } = true;

    /// <summary>
    /// Embedded ASS/SSA tracks keep the release's own style (usually white). When true, an external
    /// copy is written with white styles recoloured to <see cref="PrimaryColor"/>; all else is kept.
    /// </summary>
    public bool RestyleEmbeddedAss { get; set; } = true;

    public string FontName { get; set; } = "Arial";

    /// <summary>Font size at PlayResY 1080.</summary>
    public int FontSize { get; set; } = 64;

    public bool Bold { get; set; }

    public string PrimaryColor { get; set; } = "#FFFF00";

    public string OutlineColor { get; set; } = "#000000";

    public string ShadowColor { get; set; } = "#000000";

    public double Outline { get; set; } = 3;

    public double Shadow { get; set; } = 1.5;

    /// <summary>Vertical margin at PlayResY 1080.</summary>
    public int MarginV { get; set; } = 55;

    /// <summary>
    /// Detect and fix subtitle cache files whose stream indices were shifted by external
    /// subtitle/audio files added after extraction (Jellyfin names the cache by stream index).
    /// </summary>
    public bool RepairShiftedCache { get; set; } = true;

    /// <summary>Extract embedded subtitles when the cache for a selected track is missing (reads the whole video file).</summary>
    public bool ExtractMissing { get; set; } = true;

    /// <summary>Log what would be done without touching any file.</summary>
    public bool DryRun { get; set; }
}
