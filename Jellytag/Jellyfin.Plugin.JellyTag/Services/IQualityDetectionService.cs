using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.JellyTag.Services;

/// <summary>
/// Interface for quality detection service.
/// </summary>
public interface IQualityDetectionService
{
    /// <summary>
    /// Detects all applicable badges for an item (resolution, HDR, audio, etc.).
    /// </summary>
    /// <param name="item">The base item.</param>
    /// <returns>A list of detected badges.</returns>
    List<BadgeInfo> DetectAllBadges(BaseItem item);

    /// <summary>
    /// Clears the in-memory badge detection cache.
    /// </summary>
    void ClearBadgeCache();
}
