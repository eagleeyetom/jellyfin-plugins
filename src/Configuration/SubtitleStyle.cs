using System;

namespace Jellyfin.Plugin.PositionedSubtitles.Configuration;

/// <summary>ASS style used for positioned SRT tracks (1920x1080 canvas).</summary>
public class SubtitleStyle
{
    public string FontName { get; set; } = "Arial";

    public int FontSize { get; set; } = 64;

    public bool Bold { get; set; }

    public string PrimaryColor { get; set; } = "#FFFF00";

    public string OutlineColor { get; set; } = "#000000";

    public double Outline { get; set; } = 3;

    public string ShadowColor { get; set; } = "#000000";

    public double Shadow { get; set; } = 1.5;

    /// <summary>Draw an opaque box behind the text instead of an outline.</summary>
    public bool Background { get; set; }

    public string BackgroundColor { get; set; } = "#000000";

    /// <summary>0 (transparent) .. 100 (opaque).</summary>
    public int BackgroundOpacity { get; set; } = 60;

    public int MarginV { get; set; } = 55;

    /// <summary>Recolour white styles of embedded ASS tracks to <see cref="PrimaryColor"/>.</summary>
    public bool RecolorAss { get; set; } = true;
}

/// <summary>Style of one Jellyfin user. Users without an enabled entry get Jellyfin's normal subtitles.</summary>
public class UserStyle : SubtitleStyle
{
    /// <summary>Jellyfin user id ("N" format, no dashes).</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>For display only.</summary>
    public string UserName { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public bool Matches(Guid id) => Guid.TryParse(UserId, out var g) && g == id;
}
