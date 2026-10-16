using Jellyfin.Plugin.JellyTag.Configuration;

namespace Jellyfin.Plugin.JellyTag.Services;

/// <summary>
/// How the client is going to frame the image, which decides what part of it stays visible.
/// </summary>
public enum ImageDisplayContext
{
    /// <summary>
    /// A card whose frame matches the image type (2:3 poster, 16:9 thumbnail).
    /// </summary>
    Card,

    /// <summary>
    /// jellyfin-web list view (e.g. the episode list of a season): the image is shown with
    /// <c>background-size: cover</c> in a 3:2 frame, or 11:8 on narrow screens.
    /// </summary>
    ListView
}

/// <summary>
/// Interface for image overlay service.
/// </summary>
public interface IImageOverlayService
{
    /// <summary>
    /// Adds multiple badge overlays to an image using per-panel configuration.
    /// Returns the result stream and the content type.
    /// </summary>
    /// <param name="outputFormat">Format to encode the result in; defaults to the configured one.</param>
    Task<(Stream Stream, string ContentType)> AddBadgeOverlaysAsync(
        Stream originalImage,
        List<BadgeInfo> badges,
        ImageTypeConfig imageConfig,
        ImageDisplayContext displayContext = ImageDisplayContext.Card,
        OutputImageFormat? outputFormat = null);

    /// <summary>
    /// Determines if a badge should be shown based on the image type config panels.
    /// </summary>
    bool ShouldShowBadge(BadgeInfo badge, ImageTypeConfig imageConfig);

    /// <summary>
    /// Reloads all badge images from resources and custom badges directory.
    /// </summary>
    void ReloadBadges();
}
