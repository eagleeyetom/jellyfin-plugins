using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MetadataNotifier.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetadataNotifier.Services;

/// <summary>
/// Background service that listens for playback start events and sends media info toast notifications.
/// </summary>
public class MetadataNotifierService : IHostedService
{
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<MetadataNotifierService> _logger;

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
        _logger.LogInformation("Metadata Notifier service started.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _logger.LogInformation("Metadata Notifier service stopped.");
        return Task.CompletedTask;
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

            if (config.TargetSamsungOnly && !IsSamsungClient(session))
            {
                return;
            }

            var item = e.Item;
            if (item == null)
            {
                return;
            }

            // Short delay to allow client player UI to stabilize and transcode jobs to register
            await Task.Delay(1500).ConfigureAwait(false);

            // Re-fetch session from manager to ensure it is still playing and has updated transcoding info
            var activeSession = _sessionManager.Sessions.FirstOrDefault(s => string.Equals(s.Id, session.Id, StringComparison.Ordinal));
            if (activeSession == null || !activeSession.IsActive)
            {
                return;
            }

            session = activeSession;

            var parts = new List<string>();

            // 1. Video Dynamic Range / HDR metadata
            if (config.ShowSdr || config.ShowHdr10Plus || config.ShowHdr10 || config.ShowDolbyVision || config.ShowHlg)
            {
                var hdrInfo = GetHdrInfo(item, session, config);
                if (!string.IsNullOrEmpty(hdrInfo))
                {
                    parts.Add(hdrInfo);
                }
            }

            // 2. Audio Codec & Channels for the active track
            if (config.ShowAudio)
            {
                var audioInfo = GetAudioInfo(item, session, config);
                if (!string.IsNullOrEmpty(audioInfo))
                {
                    parts.Add(audioInfo);
                }
            }

            // 3. Transcoding or Direct Play status
            if (config.ShowTranscoding || config.ShowDirectPlay)
            {
                var transcodingInfo = GetTranscodingInfo(session);
                if (!string.IsNullOrEmpty(transcodingInfo))
                {
                    if (config.ShowTranscoding)
                    {
                        parts.Add(transcodingInfo);
                    }
                }
                else
                {
                    if (config.ShowDirectPlay)
                    {
                        parts.Add("Direct Play");
                    }
                }
            }

            // 4. Bitrate
            if (config.ShowBitrate)
            {
                var bitrateInfo = GetBitrateInfo(item, session);
                if (!string.IsNullOrEmpty(bitrateInfo))
                {
                    parts.Add(bitrateInfo);
                }
            }

            if (parts.Count == 0)
            {
                return;
            }

            var messageText = string.Join(" • ", parts);
            var header = GetNotificationHeader(item);

            _logger.LogInformation(
                "Sending metadata toast to session {SessionId} ({Client} / {Device}): {Header} | {Message}",
                session.Id,
                session.Client,
                session.DeviceName,
                header,
                messageText);

            var messageCommand = new MessageCommand
            {
                Header = header,
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

        bool isSamsung = IsSamsungClient(session);

        // Detect HDR10+ via multilayer check: VideoRangeType, stream metadata, or filename
        bool hasHdr10Plus = IsHdr10Plus(rangeType, profile, displayTitle, comment, itemPath, itemName);

        // Detect Dolby Vision via VideoRangeType, stream metadata, or filename
        bool hasDolbyVision = IsDolbyVision(rangeType, range, profile, displayTitle, itemPath);

        // Samsung TVs do not support Dolby Vision hardware decoding
        if (hasDolbyVision)
        {
            if (isSamsung && config.SuppressDvOnSamsung)
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

        return !string.IsNullOrEmpty(channelStr) ? $"{displayCodec} {channelStr}" : displayCodec;
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

    private static string? GetTranscodingInfo(SessionInfo session)
    {
        var transcodeInfo = session.TranscodingInfo;
        if (transcodeInfo == null)
        {
            return null;
        }

        var videoTranscode = !transcodeInfo.IsVideoDirect;
        var audioTranscode = !transcodeInfo.IsAudioDirect;

        if (videoTranscode && audioTranscode)
        {
            return "Transcoding (Video & Audio)";
        }

        if (videoTranscode)
        {
            return "Transcoding (Video)";
        }

        if (audioTranscode)
        {
            return "Transcoding (Audio)";
        }

        return "Direct Stream";
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
