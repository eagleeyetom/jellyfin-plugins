using Jellyfin.Plugin.MetadataNotifier.Configuration;
using Jellyfin.Plugin.MetadataNotifier.Services;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;

[assembly: DoNotParallelize]

namespace Jellyfin.Plugin.MetadataNotifier.Tests;

[TestClass]
public class MetadataFormatterTests
{
    [TestMethod]
    public void FormatAudioChannels_RecognizesOnePointZeroLayoutAsMono()
    {
        var stream = new MediaStream { ChannelLayout = "1.0" };

        Assert.AreEqual("Mono", MetadataFormatter.FormatAudioChannels(stream));
    }

    [TestMethod]
    public void GetBaseCodec_NormalizesPcmSubtypeUnlessDetailed()
    {
        Assert.AreEqual("PCM", MetadataFormatter.GetBaseCodec("PCM_S24LE", detailed: false));
        Assert.AreEqual("PCM_S24LE", MetadataFormatter.GetBaseCodec("PCM_S24LE", detailed: true));
    }

    [TestMethod]
    public void BuildMessageText_PreservesHyphenatedCodecAndCleansEmptySeparators()
    {
        var config = new PluginConfiguration
        {
            CustomTemplate = "{hdr} | {audio} • {playback} • {bitrate}"
        };

        var result = MetadataFormatter.BuildMessageText(
            config,
            "HDR10",
            "DTS-HD MA 5.1",
            string.Empty,
            "12 Mbps",
            "Title");

        Assert.AreEqual("HDR10 • DTS-HD MA 5.1 • 12 Mbps", result);
    }

    [TestMethod]
    [DataRow(1, "Mono")]
    [DataRow(2, "2.0")]
    [DataRow(6, "5.1")]
    [DataRow(8, "7.1")]
    public void FormatChannelCount_UsesFriendlyLabels(int channels, string expected)
    {
        Assert.AreEqual(expected, MetadataFormatter.FormatChannelCount(channels));
    }

    [TestMethod]
    [DataRow(900L, "900 bps")]
    [DataRow(128_000L, "128 kbps")]
    [DataRow(5_500_000L, "5.5 Mbps")]
    [DataRow(12_400_000L, "12 Mbps")]
    public void FormatBitrate_UsesReadableUnits(long bitrate, string expected)
    {
        Assert.AreEqual(expected, MetadataFormatter.FormatBitrate(bitrate));
    }

    [TestMethod]
    [DataRow("7.0", "7.0")]
    [DataRow("5.0", "5.0")]
    [DataRow("4.0", "4.0")]
    [DataRow("quad", "4.0")]
    [DataRow("3.1", "3.1")]
    [DataRow("2.1", "2.1")]
    [DataRow("stereo", "2.0")]
    public void FormatAudioChannels_RecognizesExtendedLayouts(string layout, string expected)
    {
        var stream = new MediaStream { ChannelLayout = layout };

        Assert.AreEqual(expected, MetadataFormatter.FormatAudioChannels(stream));
    }

    [TestMethod]
    public void FormatBitrate_UsesDotDecimalRegardlessOfCulture()
    {
        var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("pl-PL");
            Assert.AreEqual("5.5 Mbps", MetadataFormatter.FormatBitrate(5_500_000L));
            Assert.AreEqual("12 Mbps", MetadataFormatter.FormatBitrate(12_000_000L));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [TestMethod]
    public void FormatTranscodeReason_DescribesUnsupportedHdrRange()
    {
        Assert.AreEqual(
            "HDR range not supported",
            MetadataFormatter.FormatTranscodeReason(TranscodeReason.VideoRangeTypeNotSupported));
        Assert.AreEqual(
            "Framerate not supported",
            MetadataFormatter.FormatTranscodeReason(TranscodeReason.VideoFramerateNotSupported));
        Assert.AreEqual(
            "Audio sample rate not supported",
            MetadataFormatter.FormatTranscodeReason(TranscodeReason.AudioSampleRateNotSupported));
    }

    [TestMethod]
    public void ShouldReportToneMappedSdr_RequiresEnabledSettingAndUnsupportedRangeReason()
    {
        var transcodeInfo = new TranscodingInfo
        {
            IsVideoDirect = false,
            TranscodeReasons = TranscodeReason.VideoRangeTypeNotSupported
        };

        Assert.IsFalse(MetadataFormatter.ShouldReportToneMappedSdr(new PluginConfiguration(), transcodeInfo));

        var enabledConfig = new PluginConfiguration { DesktopSdrMode = true };
        Assert.IsTrue(MetadataFormatter.ShouldReportToneMappedSdr(enabledConfig, transcodeInfo));
        Assert.IsFalse(MetadataFormatter.ShouldReportToneMappedSdr(enabledConfig, null));

        transcodeInfo.IsVideoDirect = true;
        Assert.IsFalse(MetadataFormatter.ShouldReportToneMappedSdr(enabledConfig, transcodeInfo));
    }

    [TestMethod]
    [DataRow(true, true, "Direct Stream")]
    [DataRow(false, true, "Transcoding (Video)")]
    [DataRow(true, false, "Transcoding (Audio)")]
    [DataRow(false, false, "Transcoding (Video & Audio)")]
    public void FormatPlayback_ReportsActiveMode(bool videoDirect, bool audioDirect, string expected)
    {
        var transcodeInfo = new TranscodingInfo
        {
            IsVideoDirect = videoDirect,
            IsAudioDirect = audioDirect
        };

        Assert.AreEqual(expected, MetadataFormatter.FormatPlayback(transcodeInfo, showReasons: false));
    }

    [TestMethod]
    public void FormatPlayback_AppendsSelectedReasons()
    {
        var transcodeInfo = new TranscodingInfo
        {
            IsVideoDirect = false,
            IsAudioDirect = true,
            TranscodeReasons = TranscodeReason.VideoRangeTypeNotSupported | TranscodeReason.VideoCodecNotSupported
        };

        Assert.AreEqual(
            "Transcoding (Video): Video codec not supported, HDR range not supported",
            MetadataFormatter.FormatPlayback(transcodeInfo, showReasons: true));
    }

    [TestMethod]
    public void FormatPlayback_OmitsHdrRangeReasonWhenVideoIsDirect()
    {
        var transcodeInfo = new TranscodingInfo
        {
            IsVideoDirect = true,
            IsAudioDirect = false,
            TranscodeReasons = TranscodeReason.VideoRangeTypeNotSupported
        };

        Assert.AreEqual(
            "Transcoding (Audio)",
            MetadataFormatter.FormatPlayback(transcodeInfo, showReasons: true));
    }
}