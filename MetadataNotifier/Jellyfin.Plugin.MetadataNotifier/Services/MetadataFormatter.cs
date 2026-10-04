using System.Text.RegularExpressions;
using Jellyfin.Plugin.MetadataNotifier.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;

namespace Jellyfin.Plugin.MetadataNotifier.Services;

internal static partial class MetadataFormatter
{
    [GeneratedRegex(@"\b(?:mono|1(?:\.0)?\s*(?:ch|channels?))\b", RegexOptions.IgnoreCase)]
    private static partial Regex MonoChannelRegex();

    [GeneratedRegex(@"(?:\s*[•|]\s*|\s+-\s+)+")]
    private static partial Regex TemplateSeparatorRegex();

    [GeneratedRegex(@"(\B[A-Z])")]
    private static partial Regex CamelCaseRegex();

    internal static string BuildMessageText(
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

            formatted = TemplateSeparatorRegex().Replace(formatted, " • ")
                .Trim()
                .Trim('•', '|')
                .Trim();
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

    internal static string FormatChannelCount(int channels)
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

    internal static string FormatAudioChannels(MediaStream audioStream)
    {
        var channels = audioStream.Channels ?? 0;
        var channelLayout = audioStream.ChannelLayout ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(channelLayout))
        {
            if (channelLayout.StartsWith("7.1", StringComparison.OrdinalIgnoreCase)) return "7.1";
            if (channelLayout.StartsWith("7.0", StringComparison.OrdinalIgnoreCase)) return "7.0";
            if (channelLayout.StartsWith("6.1", StringComparison.OrdinalIgnoreCase)) return "6.1";
            if (channelLayout.StartsWith("5.1", StringComparison.OrdinalIgnoreCase)) return "5.1";
            if (channelLayout.StartsWith("5.0", StringComparison.OrdinalIgnoreCase)) return "5.0";
            if (channelLayout.StartsWith("3.1", StringComparison.OrdinalIgnoreCase)) return "3.1";
            if (channelLayout.StartsWith("2.1", StringComparison.OrdinalIgnoreCase)) return "2.1";
            if (channelLayout.Equals("stereo", StringComparison.OrdinalIgnoreCase)
                || channelLayout.StartsWith("2.0", StringComparison.OrdinalIgnoreCase)) return "2.0";
            if (channelLayout.Equals("quad", StringComparison.OrdinalIgnoreCase)
                || channelLayout.StartsWith("4.0", StringComparison.OrdinalIgnoreCase)) return "4.0";
            if (channelLayout.Equals("mono", StringComparison.OrdinalIgnoreCase)
                || channelLayout.StartsWith("1.0", StringComparison.OrdinalIgnoreCase)) return "Mono";
        }

        var channelMetadata = $"{audioStream.Title} {audioStream.DisplayTitle}";
        if (MonoChannelRegex().IsMatch(channelMetadata))
        {
            return "Mono";
        }

        return FormatChannelCount(channels);
    }

    internal static string GetBaseCodec(string codec, bool detailed)
    {
        if (!detailed && codec.StartsWith("PCM_", StringComparison.OrdinalIgnoreCase))
        {
            return "PCM";
        }

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

    internal static string FormatTranscodeReason(TranscodeReason reason)
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
            TranscodeReason.VideoFramerateNotSupported => "Framerate not supported",
            TranscodeReason.AudioSampleRateNotSupported => "Audio sample rate not supported",
            TranscodeReason.AudioBitDepthNotSupported => "Audio bit depth not supported",
            TranscodeReason.AudioProfileNotSupported => "Audio profile not supported",
            TranscodeReason.AnamorphicVideoNotSupported => "Anamorphic video not supported",
            TranscodeReason.InterlacedVideoNotSupported => "Interlaced video not supported",
            _ => CamelCaseRegex().Replace(reason.ToString(), " $1")
        };
    }

    internal static bool ShouldReportToneMappedSdr(PluginConfiguration config, TranscodingInfo? transcodeInfo)
    {
        return config.DesktopSdrMode
            && transcodeInfo?.IsVideoDirect == false
            && transcodeInfo?.TranscodeReasons.HasFlag(TranscodeReason.VideoRangeTypeNotSupported) == true;
    }

    internal static string? FormatPlayback(TranscodingInfo? transcodeInfo, bool showReasons)
    {
        if (transcodeInfo == null)
        {
            return null;
        }

        var baseMode = (transcodeInfo.IsVideoDirect, transcodeInfo.IsAudioDirect) switch
        {
            (false, false) => "Transcoding (Video & Audio)",
            (false, true) => "Transcoding (Video)",
            (true, false) => "Transcoding (Audio)",
            _ => "Direct Stream"
        };

        if (showReasons && (int)transcodeInfo.TranscodeReasons != 0)
        {
            var reasons = Enum.GetValues<TranscodeReason>()
                .Where(reason => (int)reason != 0
                    && transcodeInfo.TranscodeReasons.HasFlag(reason)
                    && (reason != TranscodeReason.VideoRangeTypeNotSupported || !transcodeInfo.IsVideoDirect))
                .Select(FormatTranscodeReason)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            var reasonsText = string.Join(", ", reasons);
            if (!string.IsNullOrEmpty(reasonsText))
            {
                return $"{baseMode}: {reasonsText}";
            }
        }

        return baseMode;
    }

    internal static string FormatBitrate(long bitrateBps)
    {
        if (bitrateBps >= 1_000_000)
        {
            double mbps = (double)bitrateBps / 1_000_000;
            return mbps >= 10
                ? FormattableString.Invariant($"{Math.Round(mbps, 0)} Mbps")
                : FormattableString.Invariant($"{Math.Round(mbps, 1)} Mbps");
        }

        if (bitrateBps >= 1_000)
        {
            return FormattableString.Invariant($"{Math.Round((double)bitrateBps / 1_000, 0)} kbps");
        }

        return $"{bitrateBps} bps";
    }
}