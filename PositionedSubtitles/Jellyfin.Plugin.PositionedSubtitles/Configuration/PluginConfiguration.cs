using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.PositionedSubtitles.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Comma separated subtitle languages (ISO 639-2 like "pol" or ISO 639-1 like "pl").</summary>
    public string Languages { get; set; } = "pol";

    /// <summary>
    /// Style subtitles on the fly (no files): for users with an enabled style, SRT tracks are
    /// announced as ASS and served converted with that style; ASS tracks are served recoloured.
    /// </summary>
    public bool LiveStyling { get; set; } = true;

    /// <summary>Client names (Authorization "Client=") for which SRT tracks are announced as ASS.</summary>
    public string LiveClients { get; set; } = "Jellyfin Web, Jellyfin for Tizen, Jellyfin for webOS";

    /// <summary>Per-user styles. Users without an enabled entry get Jellyfin's normal subtitles.</summary>
    public List<UserStyle> UserStyles { get; set; } = new();

    /// <summary>
    /// Old behaviour: write .ass files next to the videos with <see cref="Style"/>. When false, the
    /// scheduled task removes files it wrote earlier (only those carrying its marker).
    /// </summary>
    public bool WriteSidecarFiles { get; set; }

    /// <summary>Style for sidecar files, and the template for newly added users.</summary>
    public SubtitleStyle Style { get; set; } = new();

    /// <summary>Only convert tracks that contain {\anN} tags. When false every matching SRT track is converted.</summary>
    public bool OnlyPositioned { get; set; } = true;

    /// <summary>Sidecar mode only: also write recoloured copies of embedded ASS/SSA tracks.</summary>
    public bool RestyleEmbeddedAss { get; set; } = true;

    /// <summary>
    /// Detect and fix subtitle cache files whose stream indices were shifted by external
    /// subtitle/audio files added after extraction (Jellyfin names the cache by stream index).
    /// </summary>
    public bool RepairShiftedCache { get; set; } = true;

    /// <summary>Extract embedded subtitles when the cache for a selected track is missing (reads the whole video file).</summary>
    public bool ExtractMissing { get; set; } = true;

    /// <summary>Log what would be done without touching any file.</summary>
    public bool DryRun { get; set; }

    public UserStyle? FindUserStyle(Guid userId)
    {
        return userId == Guid.Empty ? null : UserStyles.FirstOrDefault(u => u.Enabled && u.Matches(userId));
    }
}
