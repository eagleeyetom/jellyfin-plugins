using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MetadataNotifier.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetadataNotifier.Services;

/// <summary>
/// Background service that listens for playback events and sends media info toast notifications.
/// </summary>
public class MetadataNotifierService : IHostedService
{
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<MetadataNotifierService> _logger;
    private readonly ConcurrentDictionary<string, int> _lastActiveAudioTrack = new(StringComparer.Ordinal);

    private static readonly Regex Hdr10PlusPathRegex = new(
        @"(?:[\.\-_\[\(]HDR10Plus[\.\-_\]\)]|[\.\-_\[\(]HDR10\+[\.\-_\]\)]|\bHDR10Plus\b|\bHDR10\+\b)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DolbyVisionPathRegex = new(
        @"(?:[\.\-_\[\(](?:DV|DOVI|Dolby[\.\-_]?Vision)[\.\-_\]\)]|\b(?:DV|DOVI|Dolby[\.\-_]?Vision)\b)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="MetadataNotifierService"/> class.
    /// </summary>
    /// <param name="sessionManager">Session manager.</param>
    /// <param name="logger">Logger.</param>
    public MetadataNotifierService(
        ISessionManager sessionManager,
        ILogger<MetadataNotifierService> logger)
    {
        _sessionManager = sessionManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        _logger.LogInformation("Metadata Notifier service started.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _lastActiveAudioTrack.Clear();
        _logger.LogInformation("Metadata Notifier service stopped.");
        return Task.CompletedTask;
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        if (e.Session != null)
        {
            _lastActiveAudioTrack.TryRemove(e.Session.Id, out _);
        }
    }

    private async void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.IsEnabled)
            {
                return;
            }

            var session = e.Session;
            if (session == null)
            {
                return;
            }

            if (IsUserExcluded(session, config))
            {
                return;
            }

            var item = e.Item;
            if (item == null || ShouldIgnoreItem(item, config))
            {
                return;
            }

            // Short delay to allow client player UI to stabilize and transcode jobs to register
            await Task.Delay(1500).ConfigureAwait(false);

            // Re-fetch session from manager to ensure it has updated transcoding info
            var activeSession = _sessionManager.Sessions.FirstOrDefault(s => string.Equals(s.Id, session.Id, StringComparison.Ordinal));
            if (activeSession != null)
            {
                session = activeSession;
            }

            // Remember initial active audio track so progress event won't immediately trigger
            int? initialAudioIndex = session.PlayState?.AudioStreamIndex;
            if (initialAudioIndex.HasValue)
            {
                _lastActiveAudioTrack[session.Id] = initialAudioIndex.Value;
            }

            var hdrInfo = string.Empty;
            if (config.ShowSdr || config.ShowHdr10Plus || config.ShowHdr10 || config.ShowDolbyVision || config.ShowHlg)
            {
                hdrInfo = GetHdrInfo(item, session, config);
            }

            var audioInfo = string.Empty;
            if (config.ShowAudio)
            {
                audioInfo = GetAudioInfo(item, session, config);
            }

            var playbackInfo = string.Empty;
            if (config.ShowTranscoding || config.ShowDirectPlay)
            {
                var transcodingInfo = GetTranscodingInfo(session, config);
                if (!string.IsNullOrEmpty(transcodingInfo))
                {
                    if (config.ShowTranscoding)
                    {
                        playbackInfo = transcodingInfo;
                    }
                }
                else
                {
                    if (config.ShowDirectPlay)
                    {
                        playbackInfo = "Direct Play";
                    }
                }
            }

            var bitrateInfo = string.Empty;
            if (config.ShowBitrate)
            {
                bitrateInfo = GetBitrateInfo(item, session);
            }

            var header = GetNotificationHeader(item);
            var messageText = BuildMessageText(config, hdrInfo, audioInfo, playbackInfo, bitrateInfo, header);

            if (string.IsNullOrWhiteSpace(messageText))
            {
                return;
            }

            _logger.LogInformation(
                "Sending metadata toast to session {SessionId} ({Client} / {Device}): {Header} | {Message}",
                session.Id,
                session.Client,
                session.DeviceName,
                header,
                messageText);

            var messageCommand = new MessageCommand
            {
                Header = string.Empty,
                Text = messageText,
                TimeoutMs = config.NotificationDurationMs
            };

            await _sessionManager.SendMessageCommand(
                session.Id,
                session.Id,
                messageCommand,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending metadata toast notification.");
        }
    }

    private async void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.IsEnabled || !config.NotifyOnAudioTrackChange)
            {
                return;
            }

            var session = e.Session;
            if (session == null || !session.IsActive)
            {
                return;
            }

            if (IsUserExcluded(session, config))
            {
                return;
            }

            var item = e.Item;
            if (item == null || ShouldIgnoreItem(item, config))
            {
                return;
            }

            int? currentAudioIndex = session.PlayState?.AudioStreamIndex;
            if (!currentAudioIndex.HasValue)
            {
                return;
            }

            if (_lastActiveAudioTrack.TryGetValue(session.Id, out var previousIndex)
                && previousIndex != currentAudioIndex.Value)
            {
                _lastActiveAudioTrack[session.Id] = currentAudioIndex.Value;

                var audioInfo = GetAudioInfo(item, session, config);
                if (string.IsNullOrEmpty(audioInfo))
                {
                    return;
                }

                var header = GetNotificationHeader(item);
                var toastText = $"Audio: {audioInfo}";

                _logger.LogInformation(
                    "Audio track changed in session {SessionId} ({Client}): {Text}",
                    session.Id,
                    session.Client,
                    toastText);

                var messageCommand = new MessageCommand
                {
                    Header = string.Empty,
                    Text = toastText,
                    TimeoutMs = config.NotificationDurationMs
                };

                await _sessionManager.SendMessageCommand(
                    session.Id,
                    session.Id,
                    messageCommand,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling audio track change event.");
        }
    }

    private static string BuildMessageText(
        PluginConfiguration config,
        string hdrInfo,
        string audioInfo,
        string playbackInfo,
        string bitrateInfo,
        string header)
    {
        if (!string.IsNullOrWhiteSpace(config.CustomTemplate))
        {
            var formatted = config.CustomTemplate
                .Replace("{hdr}", hdrInfo, StringComparison.OrdinalIgnoreCase)
                .Replace("{audio}", audioInfo, StringComparison.OrdinalIgnoreCase)
                .Replace("{playback}", playbackInfo, StringComparison.OrdinalIgnoreCase)
                .Replace("{mode}", playbackInfo, StringComparison.OrdinalIgnoreCase)
                .Replace("{bitrate}", bitrateInfo, StringComparison.OrdinalIgnoreCase)
                .Replace("{title}", header, StringComparison.OrdinalIgnoreCase);

            // Clean up any double bullets, leading/trailing bullets, or extra spaces left by empty placeholders
            formatted = Regex.Replace(formatted, @"(?:\s*[•\|\-]\s*)+", " • ").Trim(' ', '•', '|', '-');
            if (!string.IsNullOrWhiteSpace(formatted))
            {
                return formatted;
            }
        }

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(hdrInfo)) parts.Add(hdrInfo);
        if (!string.IsNullOrEmpty(audioInfo)) parts.Add(audioInfo);
        if (!string.IsNullOrEmpty(playbackInfo)) parts.Add(playbackInfo);
        if (!string.IsNullOrEmpty(bitrateInfo)) parts.Add(bitrateInfo);

        return string.Join(" • ", parts);
    }

    private static bool ShouldIgnoreItem(BaseItem item, PluginConfiguration config)
    {
        if (config.IgnoreAudioMedia && (item is Audio || item.MediaType == MediaType.Audio))
        {
            return true;
        }

        if (config.ExcludedLibraryIds != null && config.ExcludedLibraryIds.Count > 0)
        {
            var libId = GetLibraryId(item);
            if (libId.HasValue)
            {
                var idStr = libId.Value.ToString();
                var idN = libId.Value.ToString("N");
                if (config.ExcludedLibraryIds.Contains(idStr, StringComparer.OrdinalIgnoreCase)
                    || config.ExcludedLibraryIds.Contains(idN, StringComparer.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsUserExcluded(SessionInfo session, PluginConfiguration config)
    {
        if (config.ExcludedUserIds != null && config.ExcludedUserIds.Count > 0 && session.UserId != Guid.Empty)
        {
            var userIdStr = session.UserId.ToString();
            var userIdN = session.UserId.ToString("N");
            return config.ExcludedUserIds.Contains(userIdStr, StringComparer.OrdinalIgnoreCase)
                || config.ExcludedUserIds.Contains(userIdN, StringComparer.OrdinalIgnoreCase);
        }

        return false;
    }

    private static Guid? GetLibraryId(BaseItem item)
    {
        var parent = item;
        while (parent != null)
        {
            if (parent is Folder folder && folder.IsTopParent)
            {
                return folder.Id;
            }

            parent = parent.GetParent();
        }

        return item.ParentId != Guid.Empty ? item.ParentId : null;
    }

    private static string GetNotificationHeader(BaseItem item)
    {
        if (item is Episode episode)
        {
            var seriesName = episode.SeriesName;
            if (string.IsNullOrWhiteSpace(seriesName) && episode.Series != null)
            {
                seriesName = episode.Series.Name;
            }

            var seasonNum = episode.ParentIndexNumber;
            var epNum = episode.IndexNumber;

            if (!string.IsNullOrWhiteSpace(seriesName))
            {
                if (seasonNum.HasValue && epNum.HasValue)
                {
                    return $"{seriesName} S{seasonNum.Value:D2}E{epNum.Value:D2}";
                }

                return $"{seriesName} - {episode.Name}";
            }
        }

        return item.Name ?? "Media Info";
    }

    private static bool IsSamsungClient(SessionInfo session)
    {
        var client = session.Client ?? string.Empty;
        var deviceName = session.DeviceName ?? string.Empty;

        // Explicit Tizen or Samsung Smart TV client
        if (client.Contains("Tizen", StringComparison.OrdinalIgnoreCase)
            || client.Contains("Samsung Smart TV", StringComparison.OrdinalIgnoreCase)
            || client.Contains("Samsung TV", StringComparison.OrdinalIgnoreCase)
            || deviceName.Contains("Tizen", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Exclude Samsung Galaxy phones/tablets running mobile apps
        if (deviceName.Contains("Samsung", StringComparison.OrdinalIgnoreCase)
            && !deviceName.Contains("Galaxy", StringComparison.OrdinalIgnoreCase)
            && !deviceName.Contains("SM-", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool IsNonHdr10PlusClient(SessionInfo session)
    {
        var client = session.Client ?? string.Empty;
        var deviceName = session.DeviceName ?? string.Empty;

        return client.Contains("webOS", StringComparison.OrdinalIgnoreCase)
            || client.Contains("LG", StringComparison.OrdinalIgnoreCase)
            || client.Contains("Sony", StringComparison.OrdinalIgnoreCase)
            || client.Contains("Bravia", StringComparison.OrdinalIgnoreCase)
            || client.Contains("Fire", StringComparison.OrdinalIgnoreCase)
            || client.Contains("AFT", StringComparison.OrdinalIgnoreCase)
            || client.Contains("Amazon", StringComparison.OrdinalIgnoreCase)
            || client.Contains("Roku", StringComparison.OrdinalIgnoreCase)
            || deviceName.Contains("webOS", StringComparison.OrdinalIgnoreCase)
            || deviceName.Contains("LG", StringComparison.OrdinalIgnoreCase)
            || deviceName.Contains("Sony", StringComparison.OrdinalIgnoreCase)
            || deviceName.Contains("Bravia", StringComparison.OrdinalIgnoreCase)
            || deviceName.Contains("Fire", StringComparison.OrdinalIgnoreCase)
            || deviceName.Contains("AFT", StringComparison.OrdinalIgnoreCase)
            || deviceName.Contains("Amazon", StringComparison.OrdinalIgnoreCase)
            || deviceName.Contains("Roku", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNonDolbyVisionClient(SessionInfo session)
    {
        var client = session.Client ?? string.Empty;
        var deviceName = session.DeviceName ?? string.Empty;

        if (IsSamsungClient(session))
        {
            return true;
        }

        // Standard Android mobile or non-Shield Android clients
        if ((client.Contains("Android", StringComparison.OrdinalIgnoreCase) || deviceName.Contains("Android", StringComparison.OrdinalIgnoreCase))
            && !deviceName.Contains("Shield", StringComparison.OrdinalIgnoreCase)
            && !client.Contains("AndroidTV", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static string GetHdrInfo(BaseItem item, SessionInfo session, PluginConfiguration config)
    {
        var mediaStreams = item.GetMediaStreams();
        var videoStream = mediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        if (videoStream == null)
        {
            return string.Empty;
        }

        var rangeType = videoStream.VideoRangeType;
        var range = videoStream.VideoRange;
        var profile = videoStream.Profile ?? string.Empty;
        var displayTitle = videoStream.DisplayTitle ?? string.Empty;
        var comment = videoStream.Comment ?? string.Empty;
        var itemPath = item.Path ?? string.Empty;
        var itemName = item.Name ?? string.Empty;

        bool isNonDvClient = IsNonDolbyVisionClient(session);
        bool isNonHdr10Plus = IsNonHdr10PlusClient(session);

        // Detect HDR10+ via multilayer check: VideoRangeType, stream metadata, or filename
        bool hasHdr10Plus = IsHdr10Plus(rangeType, profile, displayTitle, comment, itemPath, itemName);

        // Detect Dolby Vision via VideoRangeType, stream metadata, or filename
        bool hasDolbyVision = IsDolbyVision(rangeType, range, profile, displayTitle, itemPath);

        // Non-supporting TVs/clients (e.g. LG, Sony, Amazon, Roku) do not support HDR10+ hardware decoding
        if (hasHdr10Plus && isNonHdr10Plus && config.SuppressHdr10PlusOnLg)
        {
            if (hasDolbyVision && config.ShowDolbyVision)
            {
                return config.UseDetailedVideoNames ? GetDetailedDolbyVisionInfo(profile, displayTitle) : "Dolby Vision";
            }

            if (HasHdr10BaseLayer(rangeType, range, profile, displayTitle) && config.ShowHdr10)
            {
                return config.UseDetailedVideoNames ? GetDetailedHdr10Info(profile, displayTitle) : "HDR10";
            }

            if ((rangeType == VideoRangeType.DOVIWithHLG || rangeType == VideoRangeType.HLG || displayTitle.Contains("HLG", StringComparison.OrdinalIgnoreCase)) && config.ShowHlg)
            {
                return "HLG";
            }

            if (config.ShowSdr)
            {
                return "SDR";
            }

            return string.Empty;
        }

        // Samsung and non-DV Android clients do not support Dolby Vision hardware decoding
        if (hasDolbyVision)
        {
            if (isNonDvClient && config.SuppressDvOnSamsung)
            {
                // Fall back to HDR10+ if the media contains HDR10+ metadata
                if (hasHdr10Plus && config.ShowHdr10Plus)
                {
                    return config.UseDetailedVideoNames ? GetDetailedHdr10PlusInfo(profile, displayTitle) : "HDR10+";
                }

                // Fall back to HDR10 base layer (Profile 7/8, DOVIWithHDR10, DOVIWithEL, or standard HDR)
                if (HasHdr10BaseLayer(rangeType, range, profile, displayTitle) && config.ShowHdr10)
                {
                    return config.UseDetailedVideoNames ? GetDetailedHdr10Info(profile, displayTitle) : "HDR10";
                }

                // Fall back to HLG if present
                if (rangeType == VideoRangeType.DOVIWithHLG && config.ShowHlg)
                {
                    return "HLG";
                }

                // Fall back to SDR base layer (e.g. DOVI Profile 8.2 with SDR)
                if ((rangeType == VideoRangeType.DOVIWithSDR || rangeType == VideoRangeType.SDR) && config.ShowSdr)
                {
                    return "SDR";
                }

                return string.Empty;
            }

            if (config.ShowDolbyVision)
            {
                return config.UseDetailedVideoNames ? GetDetailedDolbyVisionInfo(profile, displayTitle) : "Dolby Vision";
            }
        }

        if (hasHdr10Plus && config.ShowHdr10Plus)
        {
            return config.UseDetailedVideoNames ? GetDetailedHdr10PlusInfo(profile, displayTitle) : "HDR10+";
        }

        if (rangeType == VideoRangeType.HLG || displayTitle.Contains("HLG", StringComparison.OrdinalIgnoreCase))
        {
            if (config.ShowHlg)
            {
                return "HLG";
            }
        }

        if (rangeType == VideoRangeType.HDR10 || range == VideoRange.HDR || displayTitle.Contains("HDR10", StringComparison.OrdinalIgnoreCase))
        {
            if (config.ShowHdr10)
            {
                return config.UseDetailedVideoNames ? GetDetailedHdr10Info(profile, displayTitle) : "HDR10";
            }
        }

        if (config.ShowSdr)
        {
            return "SDR";
        }

        return string.Empty;
    }

    private static bool HasHdr10BaseLayer(VideoRangeType rangeType, VideoRange range, string profile, string displayTitle)
    {
        return rangeType is VideoRangeType.HDR10
                or VideoRangeType.DOVIWithHDR10
                or VideoRangeType.DOVIWithEL
            || range == VideoRange.HDR
            || displayTitle.Contains("HDR10", StringComparison.OrdinalIgnoreCase)
            || profile.Contains("dvhe.08", StringComparison.OrdinalIgnoreCase)
            || profile.Contains("dvh1.08", StringComparison.OrdinalIgnoreCase)
            || profile.Contains("dvhe.07", StringComparison.OrdinalIgnoreCase)
            || profile.Contains("dvh1.07", StringComparison.OrdinalIgnoreCase)
            || profile.Contains("Profile 8", StringComparison.OrdinalIgnoreCase)
            || profile.Contains("Profile 7", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDolbyVision(VideoRangeType rangeType, VideoRange range, string profile, string displayTitle, string itemPath)
    {
        return rangeType is VideoRangeType.DOVI
                or VideoRangeType.DOVIWithHDR10
                or VideoRangeType.DOVIWithHLG
                or VideoRangeType.DOVIWithSDR
                or VideoRangeType.DOVIWithEL
                or VideoRangeType.DOVIWithHDR10Plus
                or VideoRangeType.DOVIWithELHDR10Plus
            || profile.Contains("DOVI", StringComparison.OrdinalIgnoreCase)
            || profile.Contains("DOLBY VISION", StringComparison.OrdinalIgnoreCase)
            || displayTitle.Contains("Dolby Vision", StringComparison.OrdinalIgnoreCase)
            || displayTitle.Contains("DV", StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrEmpty(itemPath) && DolbyVisionPathRegex.IsMatch(itemPath));
    }

    private static bool IsHdr10Plus(VideoRangeType rangeType, string profile, string displayTitle, string comment, string itemPath, string itemName)
    {
        // 1. Jellyfin probe enum
        if (rangeType is VideoRangeType.HDR10Plus
            or VideoRangeType.DOVIWithHDR10Plus
            or VideoRangeType.DOVIWithELHDR10Plus)
        {
            return true;
        }

        // 2. Stream metadata
        var combinedMeta = $"{profile} {displayTitle} {comment}";
        if (combinedMeta.Contains("HDR10+", StringComparison.OrdinalIgnoreCase)
            || combinedMeta.Contains("HDR10 PLUS", StringComparison.OrdinalIgnoreCase)
            || combinedMeta.Contains("HDR10PLUS", StringComparison.OrdinalIgnoreCase)
            || combinedMeta.Contains("ST 2094-40", StringComparison.OrdinalIgnoreCase)
            || combinedMeta.Contains("SMPTE ST 2094", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 3. File path / release name
        if (!string.IsNullOrEmpty(itemPath) && Hdr10PlusPathRegex.IsMatch(itemPath))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(itemName) && Hdr10PlusPathRegex.IsMatch(itemName))
        {
            return true;
        }

        return false;
    }

    private static string GetDetailedDolbyVisionInfo(string profile, string displayTitle)
    {
        var combined = $"{profile} {displayTitle}";

        if (combined.Contains("dvhe.08.09", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("dvh1.08.09", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("Profile 8.1", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("8.1", StringComparison.OrdinalIgnoreCase))
        {
            return "DV Profile 8.1";
        }

        if (combined.Contains("dvhe.08.06", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("dvh1.08.06", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("Profile 8.4", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("8.4", StringComparison.OrdinalIgnoreCase))
        {
            return "DV Profile 8.4";
        }

        if (combined.Contains("dvhe.08", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("dvh1.08", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("Profile 8", StringComparison.OrdinalIgnoreCase))
        {
            return "DV Profile 8";
        }

        if (combined.Contains("dvhe.07.06", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("dvh1.07.06", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("Profile 7.6", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("7.6", StringComparison.OrdinalIgnoreCase))
        {
            return "DV Profile 7.6";
        }

        if (combined.Contains("dvhe.07", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("dvh1.07", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("Profile 7", StringComparison.OrdinalIgnoreCase))
        {
            return "DV Profile 7";
        }

        if (combined.Contains("dvhe.05", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("dvh1.05", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("Profile 5", StringComparison.OrdinalIgnoreCase))
        {
            return "DV Profile 5";
        }

        if (!string.IsNullOrEmpty(profile) && !profile.Equals("DOVI", StringComparison.OrdinalIgnoreCase))
        {
            return $"Dolby Vision ({profile})";
        }

        return "Dolby Vision";
    }

    private static string GetDetailedHdr10PlusInfo(string profile, string displayTitle)
    {
        var combined = $"{profile} {displayTitle}";
        if (combined.Contains("Profile", StringComparison.OrdinalIgnoreCase))
        {
            return $"HDR10+ ({displayTitle})";
        }

        return "HDR10+ (SMPTE ST 2094-40)";
    }

    private static string GetDetailedHdr10Info(string profile, string displayTitle)
    {
        return "HDR10 (SMPTE ST 2084)";
    }

    private static MediaStream? GetActiveAudioStream(BaseItem item, SessionInfo session)
    {
        var mediaStreams = item.GetMediaStreams();
        int? targetAudioIndex = session.PlayState?.AudioStreamIndex;

        if (targetAudioIndex.HasValue)
        {
            var activeStream = mediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Audio && s.Index == targetAudioIndex.Value);
            if (activeStream != null)
            {
                return activeStream;
            }
        }

        return mediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Audio && s.IsDefault)
            ?? mediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Audio);
    }

    private static string GetAudioInfo(BaseItem item, SessionInfo session, PluginConfiguration config)
    {
        var audioStream = GetActiveAudioStream(item, session);
        if (audioStream == null)
        {
            return string.Empty;
        }

        var codec = audioStream.Codec?.ToUpperInvariant() ?? string.Empty;
        var profile = audioStream.Profile ?? string.Empty;
        var title = audioStream.Title ?? string.Empty;
        var displayTitle = audioStream.DisplayTitle ?? string.Empty;
        var combined = $"{profile} {title} {displayTitle}";

        bool isAtmos = combined.Contains("Atmos", StringComparison.OrdinalIgnoreCase);
        bool isDtsx = combined.Contains("DTS:X", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("DTS-X", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("DTSX", StringComparison.OrdinalIgnoreCase);

        string displayCodec;

        if (isAtmos)
        {
            displayCodec = config.UseDetailedAudioNames ? $"{GetBaseCodec(codec, true)} Atmos" : "Dolby Atmos";
        }
        else if (isDtsx)
        {
            displayCodec = config.UseDetailedAudioNames ? $"{GetBaseCodec(codec, true)} DTS:X" : "DTS:X";
        }
        else if (combined.Contains("DTS-HD MA", StringComparison.OrdinalIgnoreCase)
                 || combined.Contains("DTS-HD MASTER", StringComparison.OrdinalIgnoreCase)
                 || (codec == "DTS" && profile.Equals("MA", StringComparison.OrdinalIgnoreCase)))
        {
            displayCodec = "DTS-HD MA";
        }
        else if (combined.Contains("DTS-HD HRA", StringComparison.OrdinalIgnoreCase)
                 || combined.Contains("High Resolution Audio", StringComparison.OrdinalIgnoreCase)
                 || (codec == "DTS" && profile.Equals("HRA", StringComparison.OrdinalIgnoreCase)))
        {
            displayCodec = "DTS-HD HRA";
        }
        else if (combined.Contains("DTS-HD", StringComparison.OrdinalIgnoreCase))
        {
            displayCodec = "DTS-HD";
        }
        else
        {
            displayCodec = GetBaseCodec(codec, config.UseDetailedAudioNames);
        }

        string channelStr = FormatAudioChannels(audioStream);
        string displayAudio = !string.IsNullOrEmpty(channelStr) ? $"{displayCodec} {channelStr}" : displayCodec;

        // Audio conversion visualization: e.g. "Dolby TrueHD 7.1 ➔ Dolby Digital 5.1"
        if (config.ShowAudioConversion
            && session.TranscodingInfo != null
            && !session.TranscodingInfo.IsAudioDirect
            && !string.IsNullOrWhiteSpace(session.TranscodingInfo.AudioCodec))
        {
            var targetCodec = GetBaseCodec(session.TranscodingInfo.AudioCodec.ToUpperInvariant(), config.UseDetailedAudioNames);
            var targetChannels = session.TranscodingInfo.AudioChannels.HasValue
                ? FormatChannelCount(session.TranscodingInfo.AudioChannels.Value)
                : string.Empty;

            var targetAudio = !string.IsNullOrEmpty(targetChannels) ? $"{targetCodec} {targetChannels}" : targetCodec;
            return $"{displayAudio} ➔ {targetAudio}";
        }

        return displayAudio;
    }

    private static string FormatChannelCount(int channels)
    {
        return channels switch
        {
            8 => "7.1",
            7 => "6.1",
            6 => "5.1",
            3 => "2.1",
            2 => "2.0",
            1 => "Mono",
            _ => channels > 0 ? $"{channels}ch" : string.Empty
        };
    }

    private static string FormatAudioChannels(MediaStream audioStream)
    {
        var channels = audioStream.Channels ?? 0;
        var channelLayout = audioStream.ChannelLayout ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(channelLayout))
        {
            if (channelLayout.StartsWith("7.1", StringComparison.OrdinalIgnoreCase)) return "7.1";
            if (channelLayout.StartsWith("5.1", StringComparison.OrdinalIgnoreCase)) return "5.1";
            if (channelLayout.StartsWith("6.1", StringComparison.OrdinalIgnoreCase)) return "6.1";
            if (channelLayout.Equals("stereo", StringComparison.OrdinalIgnoreCase)) return "2.0";
            if (channelLayout.Equals("mono", StringComparison.OrdinalIgnoreCase)) return "Mono";
        }

        return FormatChannelCount(channels);
    }

    private static string GetBaseCodec(string codec, bool detailed)
    {
        if (detailed)
        {
            return codec switch
            {
                "TRUEHD" or "TRUHD" => "TRUEHD",
                "EAC3" => "E-AC-3",
                "AC3" => "AC3",
                "DCA" or "DTS" => "DTS",
                "AAC" => "AAC",
                "FLAC" => "FLAC",
                "OPUS" => "OPUS",
                "VORBIS" => "VORBIS",
                "MP3" => "MP3",
                "PCM" or "LPCM" => "PCM",
                _ => codec
            };
        }

        return codec switch
        {
            "TRUEHD" or "TRUHD" => "Dolby TrueHD",
            "EAC3" => "Dolby Digital+",
            "AC3" => "Dolby Digital",
            "DCA" or "DTS" => "DTS",
            "AAC" => "AAC",
            "FLAC" => "FLAC",
            "OPUS" => "Opus",
            "VORBIS" => "Vorbis",
            "MP3" => "MP3",
            "PCM" or "LPCM" => "PCM",
            _ => codec
        };
    }

    private static string? GetTranscodingInfo(SessionInfo session, PluginConfiguration config)
    {
        var transcodeInfo = session.TranscodingInfo;
        if (transcodeInfo == null)
        {
            return null;
        }

        var videoTranscode = !transcodeInfo.IsVideoDirect;
        var audioTranscode = !transcodeInfo.IsAudioDirect;

        string baseMode;
        if (videoTranscode && audioTranscode)
        {
            baseMode = "Transcoding (Video & Audio)";
        }
        else if (videoTranscode)
        {
            baseMode = "Transcoding (Video)";
        }
        else if (audioTranscode)
        {
            baseMode = "Transcoding (Audio)";
        }
        else
        {
            baseMode = "Direct Stream";
        }

        if (config.ShowTranscodeReasons && (int)transcodeInfo.TranscodeReasons != 0)
        {
            var reasons = Enum.GetValues<TranscodeReason>()
                .Where(r => (int)r != 0 && transcodeInfo.TranscodeReasons.HasFlag(r))
                .Select(FormatTranscodeReason)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            var reasonsStr = string.Join(", ", reasons);
            if (!string.IsNullOrEmpty(reasonsStr))
            {
                return $"{baseMode}: {reasonsStr}";
            }
        }

        return baseMode;
    }

    private static string FormatTranscodeReason(TranscodeReason reason)
    {
        return reason switch
        {
            TranscodeReason.ContainerNotSupported => "Container not supported",
            TranscodeReason.VideoCodecNotSupported => "Video codec not supported",
            TranscodeReason.AudioCodecNotSupported => "Audio codec not supported",
            TranscodeReason.SubtitleCodecNotSupported => "Subtitles incompatible",
            TranscodeReason.AudioChannelsNotSupported => "Audio channels not supported",
            TranscodeReason.AudioBitrateNotSupported => "Audio bitrate exceeded",
            TranscodeReason.VideoBitrateNotSupported => "Bitrate limit exceeded",
            TranscodeReason.VideoResolutionNotSupported => "Resolution not supported",
            TranscodeReason.VideoProfileNotSupported => "Video profile not supported",
            TranscodeReason.DirectPlayError => "Direct Play error",
            TranscodeReason.SecondaryAudioNotSupported => "Secondary audio not supported",
            TranscodeReason.RefFramesNotSupported => "Reference frames not supported",
            TranscodeReason.VideoRangeTypeNotSupported => "HDR range not supported",
            _ => reason.ToString()
        };
    }

    private static string GetBitrateInfo(BaseItem item, SessionInfo session)
    {
        // 1. Check transcoding bitrate first if actively transcoding
        if (session.TranscodingInfo?.Bitrate > 0)
        {
            return FormatBitrate((long)session.TranscodingInfo.Bitrate);
        }

        // 2. Try container / media source total bitrate
        if (item is IHasMediaSources hasMediaSources)
        {
            var mediaSources = hasMediaSources.GetMediaSources(false);
            var source = mediaSources?.FirstOrDefault();
            if (source?.Bitrate is int totalBitrate && totalBitrate > 0)
            {
                return FormatBitrate(totalBitrate);
            }
        }

        if (item.TotalBitrate is int itemBitrate && itemBitrate > 0)
        {
            return FormatBitrate(itemBitrate);
        }

        // 3. Fall back to summing video stream and active audio stream
        var mediaStreams = item.GetMediaStreams();
        var videoStream = mediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        if (videoStream?.BitRate is int videoBitrate && videoBitrate > 0)
        {
            var audioStream = GetActiveAudioStream(item, session);
            long totalBps = videoBitrate + (audioStream?.BitRate ?? 0);
            return FormatBitrate(totalBps);
        }

        var fallbackAudio = GetActiveAudioStream(item, session);
        if (fallbackAudio?.BitRate is int audioOnlyBitrate && audioOnlyBitrate > 0)
        {
            return FormatBitrate(audioOnlyBitrate);
        }

        return string.Empty;
    }

    private static string FormatBitrate(long bitrateBps)
    {
        if (bitrateBps >= 1_000_000)
        {
            double mbps = (double)bitrateBps / 1_000_000;
            return mbps >= 10 ? $"{Math.Round(mbps, 0)} Mbps" : $"{Math.Round(mbps, 1)} Mbps";
        }

        if (bitrateBps >= 1_000)
        {
            return $"{Math.Round((double)bitrateBps / 1_000, 0)} kbps";
        }

        return $"{bitrateBps} bps";
    }
}
