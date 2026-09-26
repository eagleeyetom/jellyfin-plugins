using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Jellyfin.Data.Enums;
using VideoRange = Jellyfin.Data.Enums.VideoRange;
using VideoRangeType = Jellyfin.Data.Enums.VideoRangeType;

using Jellyfin.Plugin.MetadataNotifier.Configuration;

namespace Jellyfin.Plugin.MetadataNotifier.Services;

/// <summary>
/// Background service that listens for playback start events and sends media info toast notifications.
/// </summary>
public class MetadataNotifierService : IHostedService
{
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<MetadataNotifierService> _logger;

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

            await Task.Delay(1500).ConfigureAwait(false);

            var parts = new List<string>();

            if (config.ShowSdr || config.ShowHdr10Plus || config.ShowHdr10 || config.ShowDolbyVision || config.ShowHlg)
            {
                var hdrInfo = GetHdrInfo(item, session, config);
                if (!string.IsNullOrEmpty(hdrInfo))
                {
                    parts.Add(hdrInfo);
                }
            }

            if (config.ShowAudio)
            {
                var audioInfo = GetAudioInfo(item, session, config);
                if (!string.IsNullOrEmpty(audioInfo))
                {
                    parts.Add(audioInfo);
                }
            }

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
            var header = item.Name ?? "Media Info";

            _logger.LogInformation("Sending Samsung metadata toast to session {SessionId} ({Client}): {Message}", session.Id, session.Client, messageText);

            var messageCommand = new MediaBrowser.Model.Session.MessageCommand
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
            _logger.LogError(ex, "Error sending Samsung metadata toast notification.");
        }
    }


    private static bool IsSamsungClient(SessionInfo session)
    {
        var client = session.Client ?? string.Empty;
        var deviceName = session.DeviceName ?? string.Empty;

        return ContainsSamsungIndicator(client)
            || ContainsSamsungIndicator(deviceName);
    }

    private static bool ContainsSamsungIndicator(string value)
    {
        return value.Contains("Samsung", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Tizen", StringComparison.OrdinalIgnoreCase);
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

        bool isSamsung = IsSamsungClient(session);

        if (IsDolbyVision(rangeType, range, profile) || displayTitle.Contains("DV", StringComparison.OrdinalIgnoreCase) || displayTitle.Contains("Dolby Vision", StringComparison.OrdinalIgnoreCase))
        {
            if (isSamsung && config.SuppressDvOnSamsung)
            {
                if (IsHdr10Plus(rangeType, profile) || displayTitle.Contains("HDR10+", StringComparison.OrdinalIgnoreCase))
                {
                    if (config.ShowHdr10Plus) return "HDR10+";
                }
                else if (rangeType == VideoRangeType.HDR10 || range == VideoRange.HDR || displayTitle.Contains("HDR10", StringComparison.OrdinalIgnoreCase))
                {
                    if (config.ShowHdr10) return "HDR10";
                }
                return string.Empty;
            }

            if (config.ShowDolbyVision)
            {
                return config.UseDetailedNames ? GetDetailedDolbyVisionInfo(profile, displayTitle) : "Dolby Vision";
            }
        }

        if (IsHdr10Plus(rangeType, profile) || displayTitle.Contains("HDR10+", StringComparison.OrdinalIgnoreCase))
        {
            if (config.ShowHdr10Plus) return "HDR10+";
        }

        if (rangeType == VideoRangeType.HLG || displayTitle.Contains("HLG", StringComparison.OrdinalIgnoreCase))
        {
            if (config.ShowHlg) return "HLG";
        }

        if (rangeType == VideoRangeType.HDR10 || range == VideoRange.HDR || displayTitle.Contains("HDR10", StringComparison.OrdinalIgnoreCase))
        {
            if (config.ShowHdr10) return "HDR10";
        }

        if (config.ShowSdr)
        {
            return "SDR";
        }

        return string.Empty;
    }
    private static string GetDetailedDolbyVisionInfo(string profile, string displayTitle)
    {
        if (profile.Contains("dvhe.08", StringComparison.OrdinalIgnoreCase) || profile.Contains("dvhe 08", StringComparison.OrdinalIgnoreCase))
        {
            if (profile.Contains("09", StringComparison.OrdinalIgnoreCase)) return "DV Profile 8.1";
            return "DV Profile 8";
        }
        if (profile.Contains("dvhe.07", StringComparison.OrdinalIgnoreCase) || profile.Contains("dvhe 07", StringComparison.OrdinalIgnoreCase)) return "DV Profile 7";
        if (profile.Contains("dvhe.05", StringComparison.OrdinalIgnoreCase) || profile.Contains("dvhe 05", StringComparison.OrdinalIgnoreCase)) return "DV Profile 5";
        if (profile.Contains("dvh1", StringComparison.OrdinalIgnoreCase)) return "DV Profile 5";

        if (!string.IsNullOrEmpty(profile) && (profile.Contains("dv") || profile.Contains("dovi")))
        {
            return $"Dolby Vision ({profile})";
        }
        return "Dolby Vision";
    }



    private static string GetBitrateInfo(BaseItem item, SessionInfo session)
    {
        // Check transcoding bitrate first if available
        if (session.TranscodingInfo != null && session.TranscodingInfo.Bitrate > 0)
        {
            return FormatBitrate((long)session.TranscodingInfo.Bitrate);
        }

        var mediaStreams = item.GetMediaStreams();
        var videoStream = mediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        if (videoStream?.BitRate is int videoBitrate && videoBitrate > 0)
        {
            var audioStream = mediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Audio);
            long totalBps = videoBitrate + (audioStream?.BitRate ?? 0);
            return FormatBitrate(totalBps);
        }

        var audioStreamOnly = mediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Audio);
        if (audioStreamOnly?.BitRate is int audioBitrate && audioBitrate > 0)
        {
            return FormatBitrate(audioBitrate);
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
        else if (bitrateBps >= 1_000)
        {
            return $"{Math.Round((double)bitrateBps / 1_000, 0)} kbps";
        }
        return $"{bitrateBps} bps";
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

    private static string GetAudioInfo(BaseItem item, SessionInfo session, PluginConfiguration config)
    {
        var mediaStreams = item.GetMediaStreams();
        var audioStream = mediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Audio);
        if (audioStream == null)
        {
            return string.Empty;
        }

        var codec = audioStream.Codec?.ToUpperInvariant() ?? string.Empty;
        var profile = audioStream.Profile ?? string.Empty;
        var title = audioStream.Title ?? string.Empty;
        var channels = audioStream.Channels;

        string displayCodec;

        // Detect Atmos/DTS:X first (high priority)
        if (profile.Contains("Atmos", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Atmos", StringComparison.OrdinalIgnoreCase))
        {
            displayCodec = "Dolby Atmos";
        }
        else if (profile.Contains("DTS:X", StringComparison.OrdinalIgnoreCase) ||
                 profile.Contains("DTS-X", StringComparison.OrdinalIgnoreCase) ||
                 title.Contains("DTS:X", StringComparison.OrdinalIgnoreCase))
        {
            displayCodec = "DTS:X";
        }
        else if (profile.Contains("DTS-HD MA", StringComparison.OrdinalIgnoreCase) ||
                 profile.Contains("DTS-HD MASTER", StringComparison.OrdinalIgnoreCase) ||
                 title.Contains("DTS-HD MA", StringComparison.OrdinalIgnoreCase))
        {
            displayCodec = "DTS-HD MA";
        }
        else if (profile.Contains("DTS-HD", StringComparison.OrdinalIgnoreCase) ||
                 title.Contains("DTS-HD", StringComparison.OrdinalIgnoreCase))
        {
            displayCodec = "DTS-HD";
        }
        else
        {
            if (config.UseDetailedNames)
            {
                displayCodec = codec switch
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
            else
            {
                displayCodec = codec switch
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
        }

        string channelStr = channels switch
        {
            8 => "7.1",
            6 => "5.1",
            2 => "2.0",
            _ => channels > 0 ? $"{channels}ch" : string.Empty
        };

        return !string.IsNullOrEmpty(channelStr) ? $"{displayCodec} {channelStr}" : displayCodec;
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
}
