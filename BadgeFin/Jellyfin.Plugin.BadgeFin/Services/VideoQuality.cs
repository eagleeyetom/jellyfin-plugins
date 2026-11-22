namespace Jellyfin.Plugin.BadgeFin.Services;

/// <summary>
/// Video quality levels.
/// </summary>
public enum VideoQuality
{
    /// <summary>
    /// Unknown quality.
    /// </summary>
    Unknown,

    /// <summary>
    /// Standard definition (below 720p).
    /// </summary>
    SD,

    /// <summary>
    /// HD 720p (1280x720).
    /// </summary>
    HD720p,

    /// <summary>
    /// Full HD 1080p (1920x1080).
    /// </summary>
    FHD1080p,

    /// <summary>
    /// 4K UHD (3840x2160).
    /// </summary>
    UHD4K
}
