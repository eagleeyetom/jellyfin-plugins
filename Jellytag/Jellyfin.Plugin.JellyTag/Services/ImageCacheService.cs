using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTag.Services;

/// <summary>
/// Service for caching modified images.
/// </summary>
public class ImageCacheService : IImageCacheService
{
    private readonly ILogger<ImageCacheService> _logger;
    private readonly string _cachePath;
    private static volatile string? _cachedConfigFingerprint;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImageCacheService"/> class.
    /// </summary>
    public ImageCacheService(ILogger<ImageCacheService> logger)
    {
        _logger = logger;
        _cachePath = Plugin.Instance?.CacheFolderPath ?? Path.Combine(Path.GetTempPath(), "JellyTag", "cache");
        EnsureCacheDirectoryExists();

        if (Plugin.Instance != null)
        {
            Plugin.Instance.ConfigurationChanged += (_, _) => _cachedConfigFingerprint = null;
        }
    }

    /// <inheritdoc />
    public Task<Stream?> GetCachedImageAsync(Guid itemId, string badgeKey, string imageTag)
    {
        var cacheKey = GenerateCacheKey(itemId, badgeKey, imageTag);
        var cacheFilePath = GetCachePath(cacheKey);

        if (!File.Exists(cacheFilePath))
        {
            return Task.FromResult<Stream?>(null);
        }

        var config = Plugin.Instance?.Configuration;
        var cacheHours = config?.CacheDurationHours ?? 24;
        var fileInfo = new FileInfo(cacheFilePath);

        if (fileInfo.LastWriteTimeUtc.AddHours(cacheHours) < DateTime.UtcNow)
        {
            _logger.LogDebug("Cache expired for item {ItemId}", itemId);
            try
            {
                File.Delete(cacheFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete expired cache file: {Path}", cacheFilePath);
            }

            return Task.FromResult<Stream?>(null);
        }

        _logger.LogDebug("Cache hit for item {ItemId}", itemId);
        Stream stream = new FileStream(cacheFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        return Task.FromResult<Stream?>(stream);
    }

    /// <inheritdoc />
    public async Task CacheImageAsync(Guid itemId, string badgeKey, string imageTag, Stream imageStream)
    {
        var cacheKey = GenerateCacheKey(itemId, badgeKey, imageTag);
        var cachePath = GetCachePath(cacheKey);
        var tempPath = cachePath + ".tmp";

        try
        {
            EnsureCacheDirectoryExists();

            using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
            {
                await imageStream.CopyToAsync(fileStream).ConfigureAwait(false);
            }

            File.Move(tempPath, cachePath, overwrite: true);

            _logger.LogDebug("Cached image for item {ItemId} at {Path}", itemId, cachePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cache image for item {ItemId}", itemId);

            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    /// <inheritdoc />
    public string GetCacheDirectory() => _cachePath;

    internal static IEnumerable<string> EnumerateCacheFiles(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return Enumerable.Empty<string>();
        }

        return Directory.EnumerateFiles(dir, "*.*")
            .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public void ClearCache()
    {
        try
        {
            if (Directory.Exists(_cachePath))
            {
                int count = 0;
                foreach (var file in EnumerateCacheFiles(_cachePath))
                {
                    try
                    {
                        File.Delete(file);
                        count++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete cache file: {Path}", file);
                    }
                }

                _logger.LogInformation("Cleared {Count} cached images", count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear cache");
        }
    }

    /// <inheritdoc />
    public (int FileCount, long TotalSizeBytes, DateTime? OldestEntry, DateTime? NewestEntry) GetCacheStats()
    {
        try
        {
            if (!Directory.Exists(_cachePath))
            {
                return (0, 0, null, null);
            }

            var allFiles = EnumerateCacheFiles(_cachePath).Select(f => new FileInfo(f)).ToArray();

            if (allFiles.Length == 0)
            {
                return (0, 0, null, null);
            }

            var totalSize = allFiles.Sum(f => f.Length);
            var oldest = allFiles.Min(f => f.LastWriteTimeUtc);
            var newest = allFiles.Max(f => f.LastWriteTimeUtc);

            return (allFiles.Length, totalSize, oldest, newest);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get cache stats");
            return (0, 0, null, null);
        }
    }

    private string GenerateCacheKey(Guid itemId, string badgeKey, string imageTag)
    {
        var config = Plugin.Instance?.Configuration;
        var configFingerprint = config != null
            ? (_cachedConfigFingerprint ??= ComputeConfigFingerprint(config))
            : string.Empty;
        var input = $"{itemId}_{badgeKey}_{imageTag}_{configFingerprint}";

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var hash = Convert.ToHexString(hashBytes)[..16];

        return $"{itemId}_{hash}";
    }

    internal static string ComputeConfigFingerprint(Configuration.PluginConfiguration config)
    {
        var sb = new StringBuilder(256);
        AppendFingerprintValue(sb, typeof(ImageCacheService).Assembly.GetName().Version?.ToString() ?? "unknown");
        AppendFingerprintValue(sb, config.Enabled);
        AppendFingerprintValue(sb, config.HideDolbyVisionOnSamsungClients);
        AppendFingerprintValue(sb, config.HideDolbyVisionOnFireTvClients);
        AppendFingerprintValue(sb, config.HideHdrOnWindowsClients);
        AppendFingerprintValue(sb, config.WhiteLogoBackground);
        AppendFingerprintValue(sb, config.UseOriginalAacLogo);
        AppendFingerprintValue(sb, (int)config.OutputFormat);
        AppendFingerprintValue(sb, config.JpegQuality);
        AppendFingerprintValue(sb, config.WebPQuality);
        AppendFingerprintValue(sb, config.ThumbnailSameAsPoster);
        AppendFingerprintValue(sb, config.ThumbnailSizeReduction);
        AppendFingerprintValue(sb, config.DebugCountryCode);
        AppendImageTypeFingerprint(sb, config.PosterConfig);
        AppendImageTypeFingerprint(sb, config.ThumbnailConfig);
        AppendFingerprintValue(sb, config.CustomBadgeTexts?.Count ?? -1);
        if (config.CustomBadgeTexts != null)
        {
            foreach (var cbt in config.CustomBadgeTexts)
            {
                AppendFingerprintValue(sb, cbt.Key);
                AppendFingerprintValue(sb, cbt.Text);
            }
        }

        AppendFingerprintValue(sb, config.CustomBadgeColors?.Count ?? -1);
        if (config.CustomBadgeColors != null)
        {
            foreach (var cbc in config.CustomBadgeColors)
            {
                AppendFingerprintValue(sb, cbc.BadgeKey);
                AppendFingerprintValue(sb, cbc.BgColor);
                AppendFingerprintValue(sb, cbc.BgOpacity);
                AppendFingerprintValue(sb, cbc.TextColor);
                AppendFingerprintValue(sb, cbc.CornerRadius);
            }
        }

        AppendFingerprintValue(sb, config.LanguageFlagOverrides?.Count ?? -1);
        if (config.LanguageFlagOverrides != null)
        {
            foreach (var lfo in config.LanguageFlagOverrides)
            {
                AppendFingerprintValue(sb, lfo.LanguageCode);
                AppendFingerprintValue(sb, lfo.FlagCode);
            }
        }

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hashBytes)[..16];
    }

    private static void AppendImageTypeFingerprint(StringBuilder sb, Configuration.ImageTypeConfig c)
    {
        AppendFingerprintValue(sb, c.Enabled);
        AppendPanelFingerprint(sb, c.ResolutionPanel);
        AppendPanelFingerprint(sb, c.HdrPanel);
        AppendPanelFingerprint(sb, c.CodecPanel);
        AppendPanelFingerprint(sb, c.AudioPanel);
        AppendPanelFingerprint(sb, c.ChannelPanel);
        AppendPanelFingerprint(sb, c.LanguagePanel);
        AppendFingerprintValue(sb, c.ShowVostIndicator);
        AppendFingerprintValue(sb, c.VostBgColor);
        AppendFingerprintValue(sb, c.VostTextColor);
        AppendFingerprintValue(sb, c.VostBgOpacity);
        AppendFingerprintValue(sb, c.VostCornerRadius);
    }

    private static void AppendPanelFingerprint(StringBuilder sb, Configuration.BadgePanelSettings p)
    {
        AppendFingerprintValue(sb, p.Enabled);
        AppendFingerprintValue(sb, (int)p.Position);
        AppendFingerprintValue(sb, (int)p.ShowMode);
        AppendFingerprintValue(sb, (int)p.Layout);
        AppendFingerprintValue(sb, p.GapPercent);
        AppendFingerprintValue(sb, p.SizePercent);
        AppendFingerprintValue(sb, p.MarginPercent);
        AppendFingerprintValue(sb, (int)p.Style);
        AppendFingerprintValue(sb, (int)p.IconStyle);
        AppendFingerprintValue(sb, p.Order);
        AppendFingerprintValue(sb, p.TextBgColor);
        AppendFingerprintValue(sb, p.TextBgOpacity);
        AppendFingerprintValue(sb, p.TextColor);
        AppendFingerprintValue(sb, p.TextCornerRadius);
        AppendFingerprintValue(sb, p.EnabledBadges?.Count ?? -1);
        if (p.EnabledBadges != null)
        {
            foreach (var badge in p.EnabledBadges)
            {
                AppendFingerprintValue(sb, badge);
            }
        }

        AppendFingerprintValue(sb, p.DisabledLogos?.Count ?? -1);
        if (p.DisabledLogos != null)
        {
            foreach (var logo in p.DisabledLogos)
            {
                AppendFingerprintValue(sb, logo);
            }
        }

        AppendFingerprintValue(sb, p.WhiteLogoBackground);
        AppendFingerprintValue(sb, p.BadgeTypeOverrides?.Count ?? -1);
        if (p.BadgeTypeOverrides != null)
        {
            foreach (var o in p.BadgeTypeOverrides)
            {
                AppendFingerprintValue(sb, o.BadgeKey);
                AppendFingerprintValue(sb, o.BgColor);
                AppendFingerprintValue(sb, o.BgOpacity);
                AppendFingerprintValue(sb, o.TextColor);
                AppendFingerprintValue(sb, o.CornerRadius);
            }
        }
    }

    private static void AppendFingerprintValue(StringBuilder sb, object? value)
    {
        if (value == null)
        {
            sb.Append("-1:");
            return;
        }

        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        sb.Append(text.Length).Append(':').Append(text);
    }

    private string GetCachePath(string cacheKey)
    {
        var config = Plugin.Instance?.Configuration;
        var ext = config?.OutputFormat == Configuration.OutputImageFormat.WebP ? ".webp" : ".jpg";
        return Path.Combine(_cachePath, $"{cacheKey}{ext}");
    }

    private void EnsureCacheDirectoryExists()
    {
        if (!Directory.Exists(_cachePath))
        {
            Directory.CreateDirectory(_cachePath);
        }
    }
}
