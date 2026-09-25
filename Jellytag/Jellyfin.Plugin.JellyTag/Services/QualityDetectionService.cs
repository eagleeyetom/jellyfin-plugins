using System.Collections.Concurrent;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;
using VideoRange = Jellyfin.Data.Enums.VideoRange;
using VideoRangeType = Jellyfin.Data.Enums.VideoRangeType;

namespace Jellyfin.Plugin.JellyTag.Services;

/// <summary>
/// Service for detecting video quality from media items.
/// </summary>
public class QualityDetectionService : IQualityDetectionService
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<QualityDetectionService> _logger;
    private readonly ConcurrentDictionary<Guid, (List<BadgeInfo> Badges, DateTime CachedAt)> _badgeCache = new();
    private static readonly TimeSpan BadgeCacheTtl = TimeSpan.FromMinutes(5);
    private DateTime _lastCacheCleanup = DateTime.UtcNow;
    private static readonly TimeSpan CacheCleanupInterval = TimeSpan.FromMinutes(10);

    public QualityDetectionService(
        ILibraryManager libraryManager,
        ILogger<QualityDetectionService> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public VideoQuality GetQuality(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item == null)
        {
            _logger.LogDebug("Item not found: {ItemId}", itemId);
            return VideoQuality.Unknown;
        }

        return GetQualityFromItem(item);
    }

    public static VideoQuality DetermineQuality(int width, int height)
    {
        var maxDimension = Math.Max(width, height);

        if (maxDimension >= 3800) return VideoQuality.UHD4K;
        if (maxDimension >= 1900) return VideoQuality.FHD1080p;
        if (maxDimension >= 1260) return VideoQuality.HD720p;
        if (maxDimension > 0) return VideoQuality.SD;
        return VideoQuality.Unknown;
    }

    /// <inheritdoc />
    public VideoQuality GetQualityFromItem(BaseItem item)
    {
        if (item is Video video)
        {
            return GetQualityFromVideo(video);
        }

        var query = new InternalItemsQuery
        {
            ParentId = item.Id,
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
            Limit = 10
        };
        var children = _libraryManager.GetItemList(query);
        var bestQuality = VideoQuality.Unknown;
        foreach (var child in children)
        {
            if (child is Video childVideo)
            {
                var q = GetQualityFromVideo(childVideo);
                if (q != VideoQuality.Unknown && (bestQuality == VideoQuality.Unknown || q > bestQuality))
                {
                    bestQuality = q;
                    if (bestQuality == VideoQuality.UHD4K) break;
                }
            }
        }

        if (bestQuality != VideoQuality.Unknown)
        {
            _logger.LogDebug("Resolved quality {Quality} for parent item: {ItemName}", bestQuality, item.Name);
        }

        return bestQuality;
    }

    /// <inheritdoc />
    public List<BadgeInfo> DetectAllBadges(BaseItem item)
    {
        if (_badgeCache.TryGetValue(item.Id, out var cached) && DateTime.UtcNow - cached.CachedAt < BadgeCacheTtl)
        {
            return new List<BadgeInfo>(cached.Badges);
        }

        var badges = DetectAllBadgesInternal(item);
        _badgeCache[item.Id] = (badges, DateTime.UtcNow);

        // Periodically evict expired entries to prevent unbounded memory growth
        if (DateTime.UtcNow - _lastCacheCleanup > CacheCleanupInterval)
        {
            _lastCacheCleanup = DateTime.UtcNow;
            var expiredKeys = _badgeCache
                .Where(kvp => DateTime.UtcNow - kvp.Value.CachedAt > BadgeCacheTtl)
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var key in expiredKeys)
            {
                _badgeCache.TryRemove(key, out _);
            }
        }
        return badges;
    }

    /// <inheritdoc />
    public void ClearBadgeCache()
    {
        _badgeCache.Clear();
    }

    private List<BadgeInfo> DetectAllBadgesInternal(BaseItem item)
    {
        var badges = new List<BadgeInfo>();

        if (item is Video video)
        {
            DetectBadgesFromVideo(video, badges);
        }
        else
        {
            var query = new InternalItemsQuery
            {
                ParentId = item.Id,
                Recursive = true,
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
                Limit = 10
            };
            var children = _libraryManager.GetItemList(query);

            var bestResolution = VideoQuality.Unknown;
            Video? bestVideo = null;

            foreach (var child in children)
            {
                if (child is Video childVideo)
                {
                    var q = GetQualityFromVideo(childVideo);
                    if (q != VideoQuality.Unknown && (bestResolution == VideoQuality.Unknown || q > bestResolution))
                    {
                        bestResolution = q;
                        bestVideo = childVideo;
                    }

                    bestVideo ??= childVideo;
                }
            }

            if (bestResolution != VideoQuality.Unknown)
            {
                badges.Add(CreateResolutionBadge(bestResolution));
            }

            foreach (var child in children.OfType<Video>())
            {
                DetectHdrAndAudioBadges(child, badges);
            }

            DeduplicateBadges(badges);
        }

        return badges;
    }

    private void DetectBadgesFromVideo(Video video, List<BadgeInfo> badges)
    {
        DetectBadgesFromVideo(video, badges, includeResolution: true);
    }

    private void DetectHdrAndAudioBadges(Video video, List<BadgeInfo> badges)
    {
        DetectBadgesFromVideo(video, badges, includeResolution: false);
    }

    private void DetectBadgesFromVideo(Video video, List<BadgeInfo> badges, bool includeResolution)
    {
        try
        {
            var mediaSources = video.GetMediaSources(false);
            var mediaSource = mediaSources?.FirstOrDefault();
            var videoStream = mediaSource?.MediaStreams?.FirstOrDefault(s => s.Type == MediaStreamType.Video);

            if (videoStream != null)
            {
                if (includeResolution)
                {
                    var width = videoStream.Width ?? 0;
                    var height = videoStream.Height ?? 0;
                    var quality = DetermineQuality(width, height);
                    if (quality != VideoQuality.Unknown)
                    {
                        badges.Add(CreateResolutionBadge(quality));
                    }
                }

                // HDR detection - always detect, filtering happens in ShouldShowBadge
                badges.AddRange(DetectHdr(videoStream));

                // Video codec detection
                var codec = videoStream.Codec?.ToLowerInvariant() ?? string.Empty;
                if (codec is "h264" or "avc")
                {
                    badges.Add(new BadgeInfo { Category = BadgeCategory.VideoCodec, BadgeKey = "h264", ResourceFileName = "badge-h264.svg" });
                }
                else if (codec is "hevc" or "h265")
                {
                    badges.Add(new BadgeInfo { Category = BadgeCategory.VideoCodec, BadgeKey = "hevc", ResourceFileName = "badge-hevc.svg" });
                }
                else if (codec == "av1")
                {
                    badges.Add(new BadgeInfo { Category = BadgeCategory.VideoCodec, BadgeKey = "av1", ResourceFileName = "badge-av1.svg" });
                }
                else if (codec == "vp9")
                {
                    badges.Add(new BadgeInfo { Category = BadgeCategory.VideoCodec, BadgeKey = "vp9", ResourceFileName = "badge-vp9.svg" });
                }
            }

            // 3D detection
            if (video.Video3DFormat.HasValue)
            {
                badges.Add(new BadgeInfo
                {
                    Category = BadgeCategory.ThreeD,
                    BadgeKey = "3d",
                    ResourceFileName = "badge-3d.svg"
                });
            }

            // Audio detection - prefer the default audio track
            var allAudioStreams = mediaSource?.MediaStreams?.Where(s => s.Type == MediaStreamType.Audio).ToList();
            if (allAudioStreams != null && allAudioStreams.Count > 0)
            {
                var defaultStream = allAudioStreams.FirstOrDefault(s => s.IsDefault);
                var streamsToAnalyze = defaultStream != null
                    ? new List<MediaStream> { defaultStream }
                    : new List<MediaStream> { allAudioStreams[0] };
                var audioBadges = DetectAudio(streamsToAnalyze);
                badges.AddRange(audioBadges);
            }

            // Language detection - always detect all, filtering by mode happens in ShouldShowBadge
            var allStreams = mediaSource?.MediaStreams;
            if (allStreams != null)
            {
                var langBadges = DetectLanguages(allStreams.ToList());
                badges.AddRange(langBadges);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to detect badges for video: {ItemName}", video.Name);
        }
    }

    private static readonly Dictionary<string, string> LangCodeToFlag = new(StringComparer.OrdinalIgnoreCase)
    {
        { "fre", "fr" }, { "fra", "fr" }, { "en", "gb" }, { "eng", "gb" },
        { "ger", "de" }, { "deu", "de" }, { "de", "de" }, { "fr", "fr" },
        { "dut", "nl" }, { "nld", "nl" }, { "nl", "nl" },
        { "cze", "cz" }, { "ces", "cz" }, { "cs", "cz" },
        { "rum", "ro" }, { "ron", "ro" }, { "ro", "ro" },
        { "chi", "cn" }, { "zho", "cn" }, { "zh", "cn" },
        { "gre", "gr" }, { "ell", "gr" }, { "el", "gr" },
        { "may", "my" }, { "msa", "my" }, { "ms", "my" },
        { "tgl", "ph" }, { "fil", "ph" }, { "tl", "ph" },
        { "slo", "sk" }, { "slk", "sk" }, { "sk", "sk" },
        { "baq", "es" }, { "eus", "es" }, { "eu", "es" },
        { "wel", "gb-wls" }, { "cym", "gb-wls" }, { "cy", "gb-wls" },
        { "spa", "es" }, { "es", "es" },
        { "ita", "it" }, { "it", "it" },
        { "por", "pt" }, { "pt", "pt" },
        { "kor", "kr" }, { "ko", "kr" },
        { "rus", "ru" }, { "ru", "ru" },
        { "ara", "sa" }, { "ar", "sa" },
        { "hin", "in" }, { "hi", "in" },
        { "tha", "th" }, { "th", "th" },
        { "pol", "pl" }, { "pl", "pl" },
        { "tur", "tr" }, { "tr", "tr" },
        { "swe", "se" }, { "sv", "se" },
        { "dan", "dk" }, { "da", "dk" },
        { "nor", "no" }, { "no", "no" },
        { "fin", "fi" }, { "fi", "fi" },
        { "hun", "hu" }, { "hu", "hu" },
        { "ukr", "ua" }, { "uk", "ua" },
        { "vie", "vn" }, { "vi", "vn" },
        { "heb", "il" }, { "he", "il" },
        { "hrv", "hr" }, { "hr", "hr" },
        { "srp", "rs" }, { "sr", "rs" },
        { "bul", "bg" }, { "bg", "bg" },
        { "lit", "lt" }, { "lt", "lt" },
        { "lav", "lv" }, { "lv", "lv" },
        { "est", "ee" }, { "et", "ee" },
        { "jpn", "jp" }, { "ja", "jp" },
        { "ind", "id" }, { "id", "id" }
    };

    private static string GetFlagResourceFileName(string langCode)
    {
        if (string.IsNullOrEmpty(langCode)) return string.Empty;
        var lower = langCode.ToLowerInvariant();
        var countryCode = LangCodeToFlag.TryGetValue(lower, out var mapped) ? mapped : lower;
        return $"flag-{countryCode}.svg";
    }

    /// <summary>
    /// Detects all language and subtitle badges. Always detects all languages;
    /// filtering by mode (DefaultOnly/All) is done in ShouldShowBadge.
    /// </summary>
    private static List<BadgeInfo> DetectLanguages(List<MediaStream> allStreams)
    {
        var badges = new List<BadgeInfo>();
        var audioStreams = allStreams.Where(s => s.Type == MediaStreamType.Audio).ToList();
        if (audioStreams.Count == 0) return badges;

        var addedLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Detect all audio languages
        foreach (var stream in audioStreams)
        {
            var lang = stream.Language;
            if (!string.IsNullOrEmpty(lang) && addedLanguages.Add(lang))
            {
                var langLower = lang.ToLowerInvariant();
                badges.Add(new BadgeInfo
                {
                    Category = BadgeCategory.Language,
                    BadgeKey = langLower,
                    ResourceFileName = GetFlagResourceFileName(langLower)
                });
            }
        }

        // VOST indicators - always detect, filtering happens in ShouldShowBadge
        var audioLanguages = new HashSet<string>(
            audioStreams.Where(s => !string.IsNullOrEmpty(s.Language)).Select(s => s.Language!.ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);

        var subtitleStreams = allStreams.Where(s => s.Type == MediaStreamType.Subtitle).ToList();
        foreach (var sub in subtitleStreams)
        {
            var subLang = sub.Language?.ToLowerInvariant();
            if (!string.IsNullOrEmpty(subLang) && !audioLanguages.Contains(subLang))
            {
                var key = "vost" + subLang;
                if (addedLanguages.Add(key))
                {
                    badges.Add(new BadgeInfo
                    {
                        Category = BadgeCategory.Subtitle,
                        BadgeKey = key,
                        ResourceFileName = string.Empty
                    });
                }
            }
        }

        return badges;
    }

    private static List<BadgeInfo> DetectHdr(MediaStream videoStream)
    {
        var badges = new List<BadgeInfo>();
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rangeType = videoStream.VideoRangeType;
        var range = videoStream.VideoRange;
        var profile = videoStream.Profile ?? string.Empty;

        void AddHdrBadge(string badgeKey, string resourceFileName)
        {
            if (added.Add(badgeKey))
            {
                badges.Add(new BadgeInfo { Category = BadgeCategory.Hdr, BadgeKey = badgeKey, ResourceFileName = resourceFileName });
            }
        }

        if (IsDolbyVision(rangeType, range, profile))
        {
            AddHdrBadge("dv", "badge-dv.svg");
        }

        if (IsHdr10Plus(rangeType, profile))
        {
            AddHdrBadge("hdr10plus", "badge-hdr10plus.svg");
        }

        if (rangeType == VideoRangeType.HLG)
        {
            AddHdrBadge("hlg", "badge-hlg.svg");
        }

        if (rangeType == VideoRangeType.HDR10 || rangeType is VideoRangeType.DOVIWithHDR10 or VideoRangeType.DOVIWithEL)
        {
            AddHdrBadge("hdr10", "badge-hdr10.svg");
        }

        if (badges.Count == 0 && range == VideoRange.HDR)
        {
            AddHdrBadge("hdr", "badge-hdr.svg");
        }

        return badges;
    }

    private static bool IsDolbyVision(VideoRangeType rangeType, VideoRange range, string profile)
    {
        return rangeType is VideoRangeType.DOVI
                or VideoRangeType.DOVIWithHDR10
                or VideoRangeType.DOVIWithHLG
                or VideoRangeType.DOVIWithSDR
                or VideoRangeType.DOVIWithEL
                or VideoRangeType.DOVIWithHDR10Plus
                or VideoRangeType.DOVIWithELHDR10Plus
            || profile.Contains("DOVI", StringComparison.OrdinalIgnoreCase)
            || profile.Contains("DOLBY VISION", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHdr10Plus(VideoRangeType rangeType, string profile)
    {
        return rangeType is VideoRangeType.HDR10Plus
                or VideoRangeType.DOVIWithHDR10Plus
                or VideoRangeType.DOVIWithELHDR10Plus
            || profile.Contains("HDR10+", StringComparison.OrdinalIgnoreCase)
            || profile.Contains("HDR10 PLUS", StringComparison.OrdinalIgnoreCase)
            || profile.Contains("HDR10PLUS", StringComparison.OrdinalIgnoreCase);
    }

    private static void DeduplicateBadges(List<BadgeInfo> badges)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = badges.Count - 1; i >= 0; i--)
        {
            var badge = badges[i];
            var key = $"{badge.Category}:{badge.BadgeKey}";
            if (!seen.Add(key))
            {
                badges.RemoveAt(i);
            }
        }
    }

    private static List<BadgeInfo> DetectAudio(IEnumerable<MediaStream> audioStreams)
    {
        var badges = new List<BadgeInfo>();
        BadgeInfo? codecBadge = null;
        int codecPriority = -1;
        int bestChannels = 0;

        foreach (var stream in audioStreams)
        {
            var codec = stream.Codec?.ToUpperInvariant() ?? string.Empty;
            var profile = stream.Profile?.ToUpperInvariant() ?? string.Empty;
            var channels = stream.Channels ?? 0;

            if (channels > bestChannels) bestChannels = channels;

            int priority = -1;
            BadgeInfo? candidate = null;

            if (profile.Contains("ATMOS"))
            {
                priority = 7;
                candidate = new BadgeInfo { Category = BadgeCategory.Audio, BadgeKey = "atmos", ResourceFileName = "badge-atmos.svg" };
            }
            else if (codec == "TRUEHD")
            {
                priority = 6;
                candidate = new BadgeInfo { Category = BadgeCategory.Audio, BadgeKey = "truehd", ResourceFileName = "badge-truehd.svg" };
            }
            else if (profile.Contains("DTS:X") || profile.Contains("DTS-X") || profile.Contains("DTSX"))
            {
                priority = 5;
                candidate = new BadgeInfo { Category = BadgeCategory.Audio, BadgeKey = "dtsx", ResourceFileName = "badge-dtsx.svg" };
            }
            else if (profile.Contains("DTS-HD MA") || profile.Contains("DTS-HD MASTER") || (codec == "DTS" && profile.Contains("MA")))
            {
                priority = 4;
                candidate = new BadgeInfo { Category = BadgeCategory.Audio, BadgeKey = "dtshdma", ResourceFileName = "badge-dtshdma.svg" };
            }

            if (candidate != null && priority > codecPriority)
            {
                codecPriority = priority;
                codecBadge = candidate;
            }
        }

        if (codecBadge != null) badges.Add(codecBadge);

        if (bestChannels >= 8)
            badges.Add(new BadgeInfo { Category = BadgeCategory.Audio, BadgeKey = "7.1", ResourceFileName = "badge-7_1.svg" });
        else if (bestChannels >= 6)
            badges.Add(new BadgeInfo { Category = BadgeCategory.Audio, BadgeKey = "5.1", ResourceFileName = "badge-5_1.svg" });
        else if (bestChannels >= 2)
            badges.Add(new BadgeInfo { Category = BadgeCategory.Audio, BadgeKey = "stereo", ResourceFileName = "badge-stereo.svg" });

        return badges;
    }

    private static BadgeInfo CreateResolutionBadge(VideoQuality quality)
    {
        return quality switch
        {
            VideoQuality.UHD4K => new BadgeInfo { Category = BadgeCategory.Resolution, BadgeKey = "4k", ResourceFileName = "badge-4k.svg" },
            VideoQuality.FHD1080p => new BadgeInfo { Category = BadgeCategory.Resolution, BadgeKey = "1080p", ResourceFileName = "badge-1080p.svg" },
            VideoQuality.HD720p => new BadgeInfo { Category = BadgeCategory.Resolution, BadgeKey = "720p", ResourceFileName = "badge-720p.svg" },
            VideoQuality.SD => new BadgeInfo { Category = BadgeCategory.Resolution, BadgeKey = "sd", ResourceFileName = "badge-sd.svg" },
            _ => new BadgeInfo { Category = BadgeCategory.Resolution, BadgeKey = "unknown", ResourceFileName = string.Empty }
        };
    }

    private VideoQuality GetQualityFromVideo(Video video)
    {
        try
        {
            var mediaSources = video.GetMediaSources(false);
            var mediaSource = mediaSources?.FirstOrDefault();
            var videoStream = mediaSource?.MediaStreams?.FirstOrDefault(s => s.Type == MediaStreamType.Video);
            if (videoStream == null) return VideoQuality.Unknown;

            var width = videoStream.Width ?? 0;
            var height = videoStream.Height ?? 0;
            return DetermineQuality(width, height);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get media sources for video item: {ItemName}", video.Name);
            return VideoQuality.Unknown;
        }
    }
}
