using System.Collections.Concurrent;
using System.Text.RegularExpressions;
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
public partial class QualityDetectionService : IQualityDetectionService
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
        // 4K: height >= 2000 OR width >= 3200
        if (height >= 2000 || width >= 3200) return VideoQuality.UHD4K;
        
        // 1080p: height >= 1000 OR width >= 1800
        if (height >= 1000 || width >= 1800) return VideoQuality.FHD1080p;
        
        // 720p: height >= 700 OR width >= 1200
        if (height >= 700 || width >= 1200) return VideoQuality.HD720p;
        
        if (width > 0 && height > 0) return VideoQuality.SD;
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
            Limit = 50
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
                Limit = 50
            };
            var children = _libraryManager.GetItemList(query);

            var bestResolution = VideoQuality.Unknown;
            var bestHdrBadges = new List<BadgeInfo>();
            int bestHdrScore = -1;
            var bestAudioBadges = new List<BadgeInfo>();
            int bestAudioScore = -1;
            var bestChannelBadges = new List<BadgeInfo>();
            int bestChannelScore = -1;
            var otherBadges = new List<BadgeInfo>();

            foreach (var child in children)
            {
                if (child is Video childVideo)
                {
                    var q = GetQualityFromVideo(childVideo);
                    if (q != VideoQuality.Unknown && (bestResolution == VideoQuality.Unknown || q > bestResolution))
                    {
                        bestResolution = q;
                    }

                    var childBadges = new List<BadgeInfo>();
                    DetectBadgesFromVideo(childVideo, childBadges, includeResolution: false);

                    var childHdr = childBadges.Where(b => b.Category == BadgeCategory.Hdr).ToList();
                    var hdrScore = GetHdrQualityScore(childHdr);
                    if (hdrScore > bestHdrScore)
                    {
                        bestHdrScore = hdrScore;
                        bestHdrBadges = childHdr;
                    }

                    var childAudio = childBadges.Where(b => b.Category == BadgeCategory.Audio).ToList();
                    var audioScore = GetAudioQualityScore(childAudio);
                    if (audioScore > bestAudioScore)
                    {
                        bestAudioScore = audioScore;
                        bestAudioBadges = childAudio;
                    }

                    var childChannels = childBadges.Where(b => b.Category == BadgeCategory.Channels).ToList();
                    var channelScore = GetChannelQualityScore(childChannels);
                    if (channelScore > bestChannelScore)
                    {
                        bestChannelScore = channelScore;
                        bestChannelBadges = childChannels;
                    }

                    otherBadges.AddRange(childBadges.Where(b => b.Category is not (BadgeCategory.Hdr or BadgeCategory.Audio or BadgeCategory.Channels)));
                }
            }

            if (bestResolution != VideoQuality.Unknown)
            {
                badges.Add(CreateResolutionBadge(bestResolution));
            }

            badges.AddRange(bestHdrBadges);
            badges.AddRange(bestAudioBadges);
            badges.AddRange(bestChannelBadges);
            badges.AddRange(otherBadges);

            DeduplicateBadges(badges);
        }

        return badges;
    }

    internal static int GetHdrQualityScore(List<BadgeInfo> hdrBadges)
    {
        int max = -1;
        foreach (var b in hdrBadges)
        {
            int score = b.BadgeKey switch
            {
                "dv" => 4,
                "hdr10plus" => 3,
                "hdr10" => 2,
                "hlg" => 1,
                "hdr" => 0,
                _ => -1
            };
            if (score > max) max = score;
        }
        return max;
    }

    internal static int GetAudioQualityScore(List<BadgeInfo> audioBadges)
    {
        int score = 0;
        foreach (var b in audioBadges)
        {
            score += b.BadgeKey switch
            {
                "atmos" => 90,
                "dtsx" => 80,
                "truehd" => 70,
                "dtshdma" => 60,
                "flac" => 50,
                "dts" => 40,
                "eac3" => 35,
                "opus" => 30,
                "ac3" => 25,
                "aac" => 20,
                "7.1" => 8,
                "5.1" => 6,
                "stereo" => 2,
                "mono" => 1,
                _ => 0
            };
        }
        return score;
    }

    internal static int GetChannelQualityScore(List<BadgeInfo> channelBadges)
    {
        int max = -1;
        foreach (var b in channelBadges)
        {
            int score = b.BadgeKey switch
            {
                "7.1" => 4,
                "5.1" => 3,
                "stereo" => 2,
                "mono" => 1,
                _ => 0
            };
            if (score > max) max = score;
        }
        return max;
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
                badges.AddRange(DetectHdr(videoStream, video.Path ?? string.Empty, video.Name ?? string.Empty));

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
                else if (codec is "mpeg2video" or "mpeg2" or "mp2")
                {
                    badges.Add(new BadgeInfo { Category = BadgeCategory.VideoCodec, BadgeKey = "mpeg2", ResourceFileName = "badge-mpeg2.svg" });
                }
                else if (codec is "vc1" or "vc-1" or "wmv3")
                {
                    badges.Add(new BadgeInfo { Category = BadgeCategory.VideoCodec, BadgeKey = "vc1", ResourceFileName = "badge-vc1.svg" });
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

            // Audio detection - analyze streams, ignoring commentary tracks when main tracks exist
            var allAudioStreams = mediaSource?.MediaStreams?.Where(s => s.Type == MediaStreamType.Audio).ToList();
            if (allAudioStreams != null && allAudioStreams.Count > 0)
            {
                var candidateStreams = allAudioStreams.Where(s => !IsCommentaryStream(s)).ToList();
                var streamsToAnalyze = candidateStreams.Count > 0 ? candidateStreams : allAudioStreams;
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

#if DEBUG
            // Debug manual country code override
            var config = Plugin.Instance?.Configuration;
            if (!string.IsNullOrWhiteSpace(config?.DebugCountryCode))
            {
                var debugCodes = config.DebugCountryCode.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var code in debugCodes)
                {
                    var trimmed = code.Trim().ToLowerInvariant();
                    if (string.IsNullOrEmpty(trimmed)) continue;

                    var mappedFlag = GetFlagResourceFileName(trimmed);
                    badges.Add(new BadgeInfo
                    {
                        Category = BadgeCategory.Language,
                        BadgeKey = NormalizeLanguageCode(trimmed),
                        ResourceFileName = mappedFlag
                    });
                }
            }
#endif
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to detect badges for video: {ItemName}", video.Name);
        }
    }

    internal static bool IsCommentaryStream(MediaStream stream)
    {
        var combined = $"{stream.Title} {stream.DisplayTitle} {stream.Comment}";
        return combined.Contains("commentary", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("komentarz", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("description", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ISO 639-2/B and 639-2/T three-letter codes to their 639-1 two-letter equivalent.
    /// Purely mechanical aliasing, no country is implied here.
    /// </summary>
    private static readonly Dictionary<string, string> Alpha3ToAlpha2 = new(StringComparer.OrdinalIgnoreCase)
    {
        { "fre", "fr" }, { "fra", "fr" },
        { "eng", "en" },
        { "ger", "de" }, { "deu", "de" },
        { "dut", "nl" }, { "nld", "nl" },
        { "cze", "cs" }, { "ces", "cs" },
        { "rum", "ro" }, { "ron", "ro" },
        { "chi", "zh" }, { "zho", "zh" },
        { "gre", "el" }, { "ell", "el" },
        { "may", "ms" }, { "msa", "ms" },
        { "tgl", "tl" }, { "fil", "tl" },
        { "slo", "sk" }, { "slk", "sk" },
        { "baq", "eu" }, { "eus", "eu" },
        { "wel", "cy" }, { "cym", "cy" },
        { "spa", "es" }, { "ita", "it" }, { "por", "pt" }, { "kor", "ko" },
        { "rus", "ru" }, { "ara", "ar" }, { "hin", "hi" }, { "tha", "th" },
        { "pol", "pl" }, { "tur", "tr" }, { "swe", "sv" }, { "dan", "da" },
        { "nor", "no" }, { "fin", "fi" }, { "hun", "hu" }, { "ukr", "uk" },
        { "vie", "vi" }, { "heb", "he" }, { "hrv", "hr" }, { "srp", "sr" },
        { "bul", "bg" }, { "lit", "lt" }, { "lav", "lv" }, { "est", "et" },
        { "jpn", "ja" }, { "ind", "id" }, { "cat", "ca" }, { "glg", "gl" },
        { "gle", "ga" }, { "slv", "sl" }, { "alb", "sq" }, { "sqi", "sq" },
        { "ice", "is" }, { "isl", "is" }, { "mac", "mk" }, { "mkd", "mk" },
        { "per", "fa" }, { "fas", "fa" }, { "urd", "ur" }, { "ben", "bn" },
        { "tam", "ta" }, { "tel", "te" }, { "mal", "ml" }, { "kan", "kn" },
        { "mar", "mr" }, { "guj", "gu" }, { "pan", "pa" }, { "afr", "af" },
        { "swa", "sw" }, { "bel", "be" }, { "kat", "ka" }, { "geo", "ka" },
        { "arm", "hy" }, { "hye", "hy" }, { "aze", "az" }, { "kaz", "kk" },
        { "uzb", "uz" }, { "mon", "mn" }, { "nep", "ne" }, { "sin", "si" },
        { "khm", "km" }, { "lao", "lo" }, { "bur", "my" }, { "mya", "my" },
        { "mlt", "mt" }, { "bos", "bs" }, { "ltz", "lb" }, { "epo", "eo" },
        { "lat", "la" }, { "yid", "yi" }, { "nob", "nb" }, { "nno", "nn" }
    };

    /// <summary>
    /// Default country flag for a language. These are opinionated choices for languages
    /// spoken in many countries, and can be overridden per language in the plugin config.
    /// </summary>
    private static readonly Dictionary<string, string> DefaultFlagForLanguage = new(StringComparer.OrdinalIgnoreCase)
    {
        { "en", "gb" }, { "ar", "sa" }, { "hi", "in" }, { "zh", "cn" },
        { "ko", "kr" }, { "ja", "jp" }, { "cs", "cz" }, { "el", "gr" },
        { "ms", "my" }, { "tl", "ph" }, { "eu", "es-pv" }, { "cy", "gb-wls" },
        { "ca", "es-ct" }, { "gl", "es-ga" }, { "sv", "se" }, { "da", "dk" },
        { "uk", "ua" }, { "vi", "vn" }, { "he", "il" }, { "sr", "rs" },
        { "et", "ee" }, { "fa", "ir" }, { "ur", "pk" }, { "bn", "bd" },
        { "ta", "in" }, { "te", "in" }, { "ml", "in" }, { "kn", "in" },
        { "mr", "in" }, { "gu", "in" }, { "pa", "in" }, { "af", "za" },
        { "sw", "tz" }, { "ka", "ge" }, { "hy", "am" }, { "kk", "kz" },
        { "uz", "uz" }, { "ne", "np" }, { "si", "lk" }, { "km", "kh" },
        { "lo", "la" }, { "my", "mm" }, { "nb", "no" }, { "nn", "no" },
        { "sq", "al" }, { "sl", "si" }, { "ga", "ie" }, { "is", "is" },
        { "lb", "lu" }, { "yi", "il" }, { "la", "va" }, { "be", "by" }
    };

    /// <summary>
    /// Reduces any raw stream language tag to a canonical ISO 639-1 code where possible.
    /// Handles "eng", "en-US" and "en_US" alike.
    /// </summary>
    public static string NormalizeLanguageCode(string? rawLanguage)
    {
        if (string.IsNullOrWhiteSpace(rawLanguage)) return string.Empty;

        var value = rawLanguage.Trim().ToLowerInvariant();
        var sepIndex = value.IndexOfAny(new[] { '-', '_' });
        var primary = sepIndex > 0 ? value[..sepIndex] : value;

        return Alpha3ToAlpha2.TryGetValue(primary, out var alpha2) ? alpha2 : primary;
    }

    /// <summary>
    /// Extracts the region subtag from tags like "pt-BR", which tells us the flag directly.
    /// </summary>
    private static string? GetRegionSubtag(string? rawLanguage)
    {
        if (string.IsNullOrWhiteSpace(rawLanguage)) return null;

        var value = rawLanguage.Trim().ToLowerInvariant();
        var sepIndex = value.IndexOfAny(new[] { '-', '_' });
        if (sepIndex <= 0 || sepIndex == value.Length - 1) return null;

        var region = value[(sepIndex + 1)..];
        return region.Length == 2 && region.All(char.IsLetter) ? region : null;
    }

    private static string GetFlagResourceFileName(string? rawLanguage)
    {
        if (string.IsNullOrWhiteSpace(rawLanguage)) return string.Empty;

        // A region in the file's own tag beats any default we could guess.
        var region = GetRegionSubtag(rawLanguage);
        if (region != null) return $"flag-{region}.svg";

        var canonical = NormalizeLanguageCode(rawLanguage);
        if (canonical.Length == 0) return string.Empty;

        var overrides = Plugin.Instance?.Configuration?.LanguageFlagOverrides;
        var userFlag = overrides?.FirstOrDefault(o =>
            string.Equals(o.LanguageCode, canonical, StringComparison.OrdinalIgnoreCase))?.FlagCode;
        if (!string.IsNullOrWhiteSpace(userFlag)) return $"flag-{userFlag.Trim().ToLowerInvariant()}.svg";

        var countryCode = DefaultFlagForLanguage.TryGetValue(canonical, out var mapped) ? mapped : canonical;
        return $"flag-{countryCode}.svg";
    }

    /// <summary>
    /// Detects all language and subtitle badges. Always detects all languages;
    /// filtering by mode (DefaultOnly/All) is done in ShouldShowBadge.
    /// </summary>
    internal static List<BadgeInfo> DetectLanguages(List<MediaStream> allStreams)
    {
        var badges = new List<BadgeInfo>();
        var audioStreams = allStreams.Where(s => s.Type == MediaStreamType.Audio).ToList();
        if (audioStreams.Count == 0) return badges;

        var addedLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Detect all audio languages
        foreach (var stream in audioStreams)
        {
            var lang = stream.Language;
            if (string.IsNullOrEmpty(lang)) continue;

            var key = NormalizeLanguageCode(lang);
            if (key.Length == 0 || key is "und" or "zxx" or "mis" or "mul" || !addedLanguages.Add(key)) continue;

            badges.Add(new BadgeInfo
            {
                Category = BadgeCategory.Language,
                BadgeKey = key,
                ResourceFileName = GetFlagResourceFileName(lang)
            });
        }

        // VOST indicators - always detect, filtering happens in ShouldShowBadge
        var audioLanguages = new HashSet<string>(
            audioStreams.Where(s => !string.IsNullOrEmpty(s.Language)).Select(s => NormalizeLanguageCode(s.Language)),
            StringComparer.OrdinalIgnoreCase);

        var subtitleStreams = allStreams.Where(s => s.Type == MediaStreamType.Subtitle).ToList();
        foreach (var sub in subtitleStreams)
        {
            var subLang = NormalizeLanguageCode(sub.Language);
            if (subLang.Length > 0 && subLang is not ("und" or "zxx" or "mis" or "mul") && !audioLanguages.Contains(subLang))
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

    [GeneratedRegex(
        @"(?:[\.\-_\(\[](?:DV|DOVI|Dolby[\.\-_ ]?Vision)[\.\-_\)\]]|\b(?:DV|DOVI|Dolby[\.\-_ ]?Vision)\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex DolbyVisionRegex();

    internal static List<BadgeInfo> DetectHdr(MediaStream videoStream, string path, string name)
    {
        var badges = new List<BadgeInfo>();
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rangeType = videoStream.VideoRangeType;
        var range = videoStream.VideoRange;
        var profile = videoStream.Profile ?? string.Empty;
        var title = videoStream.Title ?? string.Empty;
        var displayTitle = videoStream.DisplayTitle ?? string.Empty;
        var comment = videoStream.Comment ?? string.Empty;
        var colorTransfer = videoStream.ColorTransfer ?? string.Empty;

        void AddHdrBadge(string badgeKey, string resourceFileName)
        {
            if (added.Add(badgeKey))
            {
                badges.Add(new BadgeInfo { Category = BadgeCategory.Hdr, BadgeKey = badgeKey, ResourceFileName = resourceFileName });
            }
        }

        // 1. Dolby Vision (Priority 4)
        if (IsDolbyVision(rangeType, profile, title, displayTitle, path, name))
        {
            AddHdrBadge("dv", "badge-dv.svg");
        }

        // 2. HDR10+ (Priority 3)
        if (IsHdr10Plus(rangeType, profile, title, displayTitle, comment, path, name))
        {
            AddHdrBadge("hdr10plus", "badge-hdr10plus.svg");
        }

        // 3. HDR10 (Priority 2)
        var combinedText = $"{profile} {title} {displayTitle} {comment} {path} {name}";
        var isHdr10 = rangeType == VideoRangeType.HDR10
            || rangeType is VideoRangeType.DOVIWithHDR10 or VideoRangeType.DOVIWithEL
            || rangeType is VideoRangeType.HDR10Plus or VideoRangeType.DOVIWithHDR10Plus or VideoRangeType.DOVIWithELHDR10Plus
            || string.Equals(colorTransfer, "smpte2084", StringComparison.OrdinalIgnoreCase)
            || combinedText.Contains("HDR10", StringComparison.OrdinalIgnoreCase);

        if (isHdr10)
        {
            AddHdrBadge("hdr10", "badge-hdr10.svg");
        }

        // 4. HLG (Priority 1)
        var isHlg = rangeType == VideoRangeType.HLG
            || rangeType == VideoRangeType.DOVIWithHLG
            || string.Equals(colorTransfer, "arib-std-b67", StringComparison.OrdinalIgnoreCase)
            || combinedText.Contains("HLG", StringComparison.OrdinalIgnoreCase);

        if (isHlg)
        {
            AddHdrBadge("hlg", "badge-hlg.svg");
        }

        // 5. Generic HDR (Priority 0)
        if (badges.Count == 0 && (range == VideoRange.HDR || combinedText.Contains("HDR", StringComparison.OrdinalIgnoreCase)))
        {
            AddHdrBadge("hdr", "badge-hdr.svg");
        }

        return badges;
    }

    internal static bool IsDolbyVision(VideoRangeType rangeType, string profile, string title, string displayTitle, string path, string name)
    {
        if (rangeType is VideoRangeType.DOVI
                or VideoRangeType.DOVIWithHDR10
                or VideoRangeType.DOVIWithHLG
                or VideoRangeType.DOVIWithSDR
                or VideoRangeType.DOVIWithEL
                or VideoRangeType.DOVIWithHDR10Plus
                or VideoRangeType.DOVIWithELHDR10Plus)
        {
            return true;
        }

        var combinedMeta = $"{profile} {title} {displayTitle}";
        if (DolbyVisionRegex().IsMatch(combinedMeta))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(path) && DolbyVisionRegex().IsMatch(path))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(name) && DolbyVisionRegex().IsMatch(name))
        {
            return true;
        }

        return false;
    }

    internal static bool IsHdr10Plus(VideoRangeType rangeType, string profile, string title, string displayTitle, string comment, string path, string name)
    {
        if (rangeType is VideoRangeType.HDR10Plus
                or VideoRangeType.DOVIWithHDR10Plus
                or VideoRangeType.DOVIWithELHDR10Plus)
        {
            return true;
        }

        var combined = $"{profile} {title} {displayTitle} {comment}";
        if (combined.Contains("HDR10+", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("HDR10 PLUS", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("HDR10PLUS", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("ST 2094-40", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("SMPTE ST 2094", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(path) && (path.Contains("HDR10+", StringComparison.OrdinalIgnoreCase) || path.Contains("HDR10PLUS", StringComparison.OrdinalIgnoreCase) || path.Contains("HDR10.Plus", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(name) && (name.Contains("HDR10+", StringComparison.OrdinalIgnoreCase) || name.Contains("HDR10PLUS", StringComparison.OrdinalIgnoreCase) || name.Contains("HDR10.Plus", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    internal static void DeduplicateBadges(List<BadgeInfo> badges)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < badges.Count; i++)
        {
            var badge = badges[i];
            var key = $"{badge.Category}:{badge.BadgeKey}";
            if (!seen.Add(key))
            {
                badges.RemoveAt(i);
                i--;
            }
        }
    }

    internal static List<BadgeInfo> DetectAudio(IEnumerable<MediaStream> audioStreams)
    {
        var badges = new List<BadgeInfo>();
        BadgeInfo? codecBadge = null;
        int codecPriority = -1;
        int bestChannels = 0;

        foreach (var stream in audioStreams)
        {
            var codec = stream.Codec?.ToUpperInvariant() ?? string.Empty;
            var profile = stream.Profile ?? string.Empty;
            var title = stream.Title ?? string.Empty;
            var displayTitle = stream.DisplayTitle ?? string.Empty;
            var comment = stream.Comment ?? string.Empty;
            var combined = $"{profile} {title} {displayTitle} {comment}";
            var channels = stream.Channels ?? 0;
            var layout = stream.ChannelLayout ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(layout))
            {
                if (layout.StartsWith("7.1", StringComparison.OrdinalIgnoreCase) || layout.StartsWith("7.0", StringComparison.OrdinalIgnoreCase)) channels = Math.Max(channels, 8);
                else if (layout.StartsWith("6.1", StringComparison.OrdinalIgnoreCase) || layout.StartsWith("5.1", StringComparison.OrdinalIgnoreCase) || layout.StartsWith("5.0", StringComparison.OrdinalIgnoreCase)) channels = Math.Max(channels, 6);
                else if (layout.Equals("quad", StringComparison.OrdinalIgnoreCase) || layout.StartsWith("4.0", StringComparison.OrdinalIgnoreCase)) channels = Math.Max(channels, 4);
                else if (layout.Equals("stereo", StringComparison.OrdinalIgnoreCase) || layout.StartsWith("2.0", StringComparison.OrdinalIgnoreCase)) channels = Math.Max(channels, 2);
                else if (layout.Equals("mono", StringComparison.OrdinalIgnoreCase) || layout.StartsWith("1.0", StringComparison.OrdinalIgnoreCase)) channels = Math.Max(channels, 1);
            }
            if (channels == 0 && (combined.Contains("Mono", StringComparison.OrdinalIgnoreCase) || combined.Contains("1.0", StringComparison.OrdinalIgnoreCase)))
            {
                channels = 1;
            }

            if (channels > bestChannels) bestChannels = channels;

            int priority = -1;
            BadgeInfo? candidate = null;

            if (combined.Contains("ATMOS", StringComparison.OrdinalIgnoreCase))
            {
                priority = 90;
                candidate = new BadgeInfo { Category = BadgeCategory.Audio, BadgeKey = "atmos", ResourceFileName = "badge-atmos.svg" };
            }
            else if (combined.Contains("DTS:X", StringComparison.OrdinalIgnoreCase) || combined.Contains("DTS-X", StringComparison.OrdinalIgnoreCase) || combined.Contains("DTSX", StringComparison.OrdinalIgnoreCase))
            {
                priority = 80;
                candidate = new BadgeInfo { Category = BadgeCategory.Audio, BadgeKey = "dtsx", ResourceFileName = "badge-dtsx.svg" };
            }
            else if (codec is "TRUEHD" or "TRUHD" || combined.Contains("TRUEHD", StringComparison.OrdinalIgnoreCase))
            {
                priority = 70;
                candidate = new BadgeInfo { Category = BadgeCategory.Audio, BadgeKey = "truehd", ResourceFileName = "badge-truehd.svg" };
            }
            else if (combined.Contains("DTS-HD MA", StringComparison.OrdinalIgnoreCase) || combined.Contains("DTS-HD MASTER", StringComparison.OrdinalIgnoreCase) || (codec == "DTS" && profile.Contains("MA", StringComparison.OrdinalIgnoreCase)))
            {
                priority = 60;
                candidate = new BadgeInfo { Category = BadgeCategory.Audio, BadgeKey = "dtshdma", ResourceFileName = "badge-dtshdma.svg" };
            }
            else if (codec == "FLAC" || combined.Contains("FLAC", StringComparison.OrdinalIgnoreCase))
            {
                priority = 50;
                candidate = new BadgeInfo { Category = BadgeCategory.Audio, BadgeKey = "flac", ResourceFileName = "badge-flac.svg" };
            }
            else if (combined.Contains("DTS-HD", StringComparison.OrdinalIgnoreCase) || (codec == "DTS" && (profile.Contains("HRA", StringComparison.OrdinalIgnoreCase) || profile.Contains("HR", StringComparison.OrdinalIgnoreCase))) || codec is "DTS" or "DCA" || combined.Contains("DTS", StringComparison.OrdinalIgnoreCase))
            {
                priority = 40;
                candidate = new BadgeInfo { Category = BadgeCategory.Audio, BadgeKey = "dts", ResourceFileName = "badge-dts.svg" };
            }
            else if (codec is "EAC3" or "E-AC-3" || profile.Contains("E-AC-3", StringComparison.OrdinalIgnoreCase) || combined.Contains("EAC3", StringComparison.OrdinalIgnoreCase) || combined.Contains("DD+", StringComparison.OrdinalIgnoreCase) || combined.Contains("DOLBY DIGITAL PLUS", StringComparison.OrdinalIgnoreCase))
            {
                priority = 35;
                candidate = new BadgeInfo { Category = BadgeCategory.Audio, BadgeKey = "eac3", ResourceFileName = "badge-eac3.svg" };
            }
            else if (codec == "OPUS" || codec.Contains("OPUS") || combined.Contains("OPUS", StringComparison.OrdinalIgnoreCase))
            {
                priority = 30;
                candidate = new BadgeInfo { Category = BadgeCategory.Audio, BadgeKey = "opus", ResourceFileName = "badge-opus.svg" };
            }
            else if (codec is "AC3" or "AC-3" || combined.Contains("AC3", StringComparison.OrdinalIgnoreCase) || combined.Contains("DOLBY DIGITAL", StringComparison.OrdinalIgnoreCase))
            {
                priority = 25;
                candidate = new BadgeInfo { Category = BadgeCategory.Audio, BadgeKey = "ac3", ResourceFileName = "badge-ac3.svg" };
            }
            else if (codec == "AAC" || combined.Contains("AAC", StringComparison.OrdinalIgnoreCase))
            {
                priority = 20;
                candidate = new BadgeInfo { Category = BadgeCategory.Audio, BadgeKey = "aac", ResourceFileName = "badge-aac.svg" };
            }

            if (candidate != null && priority > codecPriority)
            {
                codecPriority = priority;
                codecBadge = candidate;
            }
        }

        if (codecBadge != null) badges.Add(codecBadge);

        if (bestChannels >= 7)
            badges.Add(new BadgeInfo { Category = BadgeCategory.Channels, BadgeKey = "7.1", ResourceFileName = "badge-7_1.svg" });
        else if (bestChannels >= 5)
            badges.Add(new BadgeInfo { Category = BadgeCategory.Channels, BadgeKey = "5.1", ResourceFileName = "badge-5_1.svg" });
        else if (bestChannels >= 2)
            badges.Add(new BadgeInfo { Category = BadgeCategory.Channels, BadgeKey = "stereo", ResourceFileName = "badge-stereo.svg" });
        else if (bestChannels == 1)
            badges.Add(new BadgeInfo { Category = BadgeCategory.Channels, BadgeKey = "mono", ResourceFileName = "badge-mono.svg" });

        return badges;
    }

    internal static BadgeInfo CreateResolutionBadge(VideoQuality quality)
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
