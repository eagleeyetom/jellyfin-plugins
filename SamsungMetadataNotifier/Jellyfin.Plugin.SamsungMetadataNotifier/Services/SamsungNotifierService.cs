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

using Jellyfin.Plugin.SamsungMetadataNotifier.Configuration;

namespace Jellyfin.Plugin.SamsungMetadataNotifier.Services;

/// <summary>
/// Background service that listens for playback start events and sends media info toast notifications.
/// </summary>
public class SamsungNotifierService : IHostedService
{
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<SamsungNotifierService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SamsungNotifierService"/> class.
    /// </summary>
    /// <param name="sessionManager">Session manager.</param>
    /// <param name="logger">Logger.</param>
    public SamsungNotifierService(
        ISessionManager sessionManager,
        ILogger<SamsungNotifierService> logger)
    {
        _sessionManager = sessionManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart += OnPlaybackStart;
        _logger.LogInformation("Samsung Metadata Notifier service started.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _logger.LogInformation("Samsung Metadata Notifier service stopped.");
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

            if (config.ShowHdr10Plus || config.ShowHdr10 || config.ShowDolbyVision || config.ShowHlg)
            {
                var hdrInfo = GetHdrInfo(item, session, config);
                if (!string.IsNullOrEmpty(hdrInfo))
                {
                    parts.Add(hdrInfo);
                }
            }

            if (config.ShowAudio)
            {
                var audioInfo = GetAudioInfo(item, session);
                if (!string.IsNullOrEmpty(audioInfo))
                {
                    parts.Add(audioInfo);
                }
            }

            if (config.ShowTranscoding)
            {
                var transcodingInfo = GetTranscodingInfo(session);
                if (!string.IsNullOrEmpty(transcodingInfo))
                {
                    parts.Add(transcodingInfo);
                }
                else
                {
                    parts.Add("Direct Play");
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
                return "Dolby Vision";
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

        return string.Empty;
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

    private static string GetAudioInfo(BaseItem item, SessionInfo session)
    {
        var mediaStreams = item.GetMediaStreams();
        var audioStream = mediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Audio);
        if (audioStream == null)
        {
            return string.Empty;
        }

        var codec = audioStream.Codec?.ToUpperInvariant() ?? string.Empty;
        var profile = audioStream.Profile ?? string.Empty;
        var channels = audioStream.Channels;

        if (codec == "TRUEHD" || codec == "TRUHD")
        {
            codec = "TrueHD";
        }
        else if (codec == "DCA")
        {
            codec = "DTS";
        }
        else if (codec == "AC3")
        {
            codec = "AC3";
        }
        else if (codec == "EAC3")
        {
            codec = "E-AC3";
        }
        else if (codec == "AAC")
        {
            codec = "AAC";
        }
        else if (codec == "FLAC")
        {
            codec = "FLAC";
        }

        if (profile.Contains("Atmos", StringComparison.OrdinalIgnoreCase))
        {
            codec += " Atmos";
        }
        else if (profile.Contains("DTS-HD MA", StringComparison.OrdinalIgnoreCase) || profile.Contains("DTS-HD", StringComparison.OrdinalIgnoreCase))
        {
            codec = "DTS-HD MA";
        }
        else if (profile.Contains("DTS:X", StringComparison.OrdinalIgnoreCase))
        {
            codec = "DTS:X";
        }

        string channelStr = channels switch
        {
            8 => "7.1",
            6 => "5.1",
            2 => "2.0",
            _ => channels > 0 ? $"{channels}ch" : string.Empty
        };

        return !string.IsNullOrEmpty(channelStr) ? $"{codec} {channelStr}" : codec;
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
