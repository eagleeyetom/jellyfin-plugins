using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyTag.Services;
using MediaBrowser.Model.Entities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: Parallelize(Workers = 0, Scope = ExecutionScope.MethodLevel)]

namespace Jellyfin.Plugin.JellyTag.Tests;

[TestClass]
public class QualityDetectionTests
{
    [TestMethod]
    [DataRow(3840, 2160, VideoQuality.UHD4K)]
    [DataRow(3840, 1600, VideoQuality.UHD4K)] // Ultrawide 4K crop
    [DataRow(1920, 1080, VideoQuality.FHD1080p)]
    [DataRow(1920, 800, VideoQuality.FHD1080p)]  // Ultrawide 1080p crop
    [DataRow(1280, 720, VideoQuality.HD720p)]
    [DataRow(1280, 534, VideoQuality.HD720p)]   // 2.39:1 720p crop
    [DataRow(720, 480, VideoQuality.SD)]
    [DataRow(640, 480, VideoQuality.SD)]
    [DataRow(0, 0, VideoQuality.Unknown)]
    [DataRow(-1, -1, VideoQuality.Unknown)]
    public void DetermineQuality_ReturnsExpectedResolution(int width, int height, VideoQuality expected)
    {
        var quality = QualityDetectionService.DetermineQuality(width, height);
        Assert.AreEqual(expected, quality);
    }

    [TestMethod]
    [DataRow(VideoQuality.UHD4K, "4k", "badge-4k.svg")]
    [DataRow(VideoQuality.FHD1080p, "1080p", "badge-1080p.svg")]
    [DataRow(VideoQuality.HD720p, "720p", "badge-720p.svg")]
    [DataRow(VideoQuality.SD, "sd", "badge-sd.svg")]
    public void CreateResolutionBadge_ProducesCorrectBadge(VideoQuality quality, string expectedKey, string expectedFile)
    {
        var badge = QualityDetectionService.CreateResolutionBadge(quality);
        Assert.AreEqual(BadgeCategory.Resolution, badge.Category);
        Assert.AreEqual(expectedKey, badge.BadgeKey);
        Assert.AreEqual(expectedFile, badge.ResourceFileName);
    }

    [TestMethod]
    public void IsDolbyVision_ValidDVTitlesAndPaths_ReturnsTrue()
    {
        Assert.IsTrue(QualityDetectionService.IsDolbyVision(VideoRangeType.DOVI, "", "", "", "", ""));
        Assert.IsTrue(QualityDetectionService.IsDolbyVision(VideoRangeType.DOVIWithHDR10, "", "", "", "", ""));
        Assert.IsTrue(QualityDetectionService.IsDolbyVision(VideoRangeType.DOVIWithHLG, "", "", "", "", ""));
        Assert.IsTrue(QualityDetectionService.IsDolbyVision(VideoRangeType.DOVIWithHDR10Plus, "", "", "", "", ""));
        Assert.IsTrue(QualityDetectionService.IsDolbyVision(VideoRangeType.Unknown, "", "", "", "Movie.2023.2160p.UHD.BluRay.x265.DV.HDR.mkv", "Movie"));
        Assert.IsTrue(QualityDetectionService.IsDolbyVision(VideoRangeType.Unknown, "", "", "", "/movies/Movie [DOVI].mkv", "Movie"));
        Assert.IsTrue(QualityDetectionService.IsDolbyVision(VideoRangeType.Unknown, "", "", "", "/movies/Movie Dolby Vision.mkv", "Movie"));
        Assert.IsTrue(QualityDetectionService.IsDolbyVision(VideoRangeType.Unknown, "Main 10 (Dolby Vision)", "", "", "", ""));
    }

    [TestMethod]
    public void IsDolbyVision_FalsePositivesLikeDvdAndDvb_ReturnsFalse()
    {
        // DVD, DVB, DVB-T, Adventure, DVDRip should NOT match DV
        Assert.IsFalse(QualityDetectionService.IsDolbyVision(VideoRangeType.SDR, "", "", "", "/movies/My.Great.Adventure.1998.DVD.mkv", "Adventure"));
        Assert.IsFalse(QualityDetectionService.IsDolbyVision(VideoRangeType.SDR, "", "", "", "/movies/Recording.DVB-T.ts", "Recording DVB-T"));
        Assert.IsFalse(QualityDetectionService.IsDolbyVision(VideoRangeType.SDR, "", "", "", "/movies/Classic.DVDRip.x264.mkv", "Classic DVDRip"));
    }

    [TestMethod]
    public void IsHdr10Plus_DetectsCorrectly()
    {
        Assert.IsTrue(QualityDetectionService.IsHdr10Plus(VideoRangeType.HDR10Plus, "", "", "", "", "", ""));
        Assert.IsTrue(QualityDetectionService.IsHdr10Plus(VideoRangeType.DOVIWithHDR10Plus, "", "", "", "", "", ""));
        Assert.IsTrue(QualityDetectionService.IsHdr10Plus(VideoRangeType.Unknown, "", "", "", "", "Movie.2022.HDR10+.mkv", "Movie"));
        Assert.IsTrue(QualityDetectionService.IsHdr10Plus(VideoRangeType.Unknown, "", "", "", "SMPTE ST 2094 metadata", "", ""));
        Assert.IsFalse(QualityDetectionService.IsHdr10Plus(VideoRangeType.HDR10, "", "", "", "", "Movie.2022.HDR10.mkv", "Movie"));
    }

    [TestMethod]
    public void DetectHdr_DolbyVisionWithHdr10Base_DetectsBothBadges()
    {
        var stream = new MediaStream
        {
            Type = MediaStreamType.Video,
            ColorTransfer = "smpte2084",
            Title = "Dolby Vision"
        };

        var badges = QualityDetectionService.DetectHdr(stream, "", "");

        Assert.HasCount(2, badges);
        Assert.IsTrue(badges.Any(b => b.BadgeKey == "dv"));
        Assert.IsTrue(badges.Any(b => b.BadgeKey == "hdr10"));
    }

    [TestMethod]
    public void DetectHdr_ColorTransferFallback_DetectsHdr10AndHlg()
    {
        var hdr10Stream = new MediaStream
        {
            Type = MediaStreamType.Video,
            ColorTransfer = "smpte2084"
        };
        var hdr10Badges = QualityDetectionService.DetectHdr(hdr10Stream, "", "");
        Assert.IsTrue(hdr10Badges.Any(b => b.BadgeKey == "hdr10"));

        var hlgStream = new MediaStream
        {
            Type = MediaStreamType.Video,
            ColorTransfer = "arib-std-b67"
        };
        var hlgBadges = QualityDetectionService.DetectHdr(hlgStream, "", "");
        Assert.IsTrue(hlgBadges.Any(b => b.BadgeKey == "hlg"));
    }

    [TestMethod]
    public void DetectHdr_GenericHdrFallback()
    {
        var stream = new MediaStream
        {
            Type = MediaStreamType.Video,
            Title = "High Dynamic Range (HDR)"
        };
        var badges = QualityDetectionService.DetectHdr(stream, "", "");
        Assert.HasCount(1, badges);
        Assert.AreEqual("hdr", badges[0].BadgeKey);
    }

    [TestMethod]
    public void GetHdrQualityScore_RespectsHierarchy()
    {
        var dvBadge = new List<BadgeInfo> { new() { BadgeKey = "dv" } };
        var hdr10PlusBadge = new List<BadgeInfo> { new() { BadgeKey = "hdr10plus" } };
        var hdr10Badge = new List<BadgeInfo> { new() { BadgeKey = "hdr10" } };
        var hlgBadge = new List<BadgeInfo> { new() { BadgeKey = "hlg" } };
        var hdrBadge = new List<BadgeInfo> { new() { BadgeKey = "hdr" } };

#pragma warning disable MSTEST0037
        Assert.IsTrue(QualityDetectionService.GetHdrQualityScore(dvBadge) > QualityDetectionService.GetHdrQualityScore(hdr10PlusBadge));
        Assert.IsTrue(QualityDetectionService.GetHdrQualityScore(hdr10PlusBadge) > QualityDetectionService.GetHdrQualityScore(hdr10Badge));
        Assert.IsTrue(QualityDetectionService.GetHdrQualityScore(hdr10Badge) > QualityDetectionService.GetHdrQualityScore(hlgBadge));
        Assert.IsTrue(QualityDetectionService.GetHdrQualityScore(hlgBadge) > QualityDetectionService.GetHdrQualityScore(hdrBadge));
#pragma warning restore MSTEST0037
    }

    [TestMethod]
    [DataRow("TRUEHD", "Atmos in title", "atmos", 8, "7.1")]
    [DataRow("DTS", "DTS:X Master Audio", "dtsx", 8, "7.1")]
    [DataRow("TRUEHD", "", "truehd", 6, "5.1")]
    [DataRow("DTS", "DTS-HD MA 7.1", "dtshdma", 8, "7.1")]
    [DataRow("FLAC", "", "flac", 6, "5.1")]
    [DataRow("DTS", "DTS-HD HRA", "dts", 6, "5.1")]
    [DataRow("EAC3", "Dolby Digital Plus", "eac3", 6, "5.1")]
    [DataRow("OPUS", "", "opus", 2, "stereo")]
    [DataRow("AC3", "Dolby Digital 5.1", "ac3", 6, "5.1")]
    [DataRow("AAC", "Stereo", "aac", 2, "stereo")]
    public void DetectAudio_DetectsCodecsAndChannels(string codec, string title, string expectedCodec, int channels, string expectedChannelBadge)
    {
        var stream = new MediaStream
        {
            Type = MediaStreamType.Audio,
            Codec = codec,
            Title = title,
            Channels = channels
        };

        var badges = QualityDetectionService.DetectAudio([stream]);

        Assert.IsTrue(badges.Any(b => b.BadgeKey == expectedCodec), $"Expected codec {expectedCodec}");
        Assert.IsTrue(badges.Any(b => b.BadgeKey == expectedChannelBadge), $"Expected channels {expectedChannelBadge}");
    }

    [TestMethod]
    public void DetectAudio_5ChannelAudio_MapsToSurround51_NotStereo()
    {
        // 5.0 channel audio layout should map to 5.1 surround badge, NOT stereo
        var stream = new MediaStream
        {
            Type = MediaStreamType.Audio,
            Codec = "AC3",
            Channels = 5,
            ChannelLayout = "5.0"
        };

        var badges = QualityDetectionService.DetectAudio([stream]);

        Assert.IsTrue(badges.Any(b => b.BadgeKey == "5.1"), "5.0 layout should be treated as 5.1 surround");
        Assert.IsFalse(badges.Any(b => b.BadgeKey == "stereo"), "5.0 layout must NOT be treated as stereo");
    }

    [TestMethod]
    public void DetectAudio_70ChannelAudio_MapsTo71Surround()
    {
        var stream = new MediaStream
        {
            Type = MediaStreamType.Audio,
            Codec = "DTS",
            Channels = 7,
            ChannelLayout = "7.0"
        };

        var badges = QualityDetectionService.DetectAudio([stream]);
        Assert.IsTrue(badges.Any(b => b.BadgeKey == "7.1"));
    }

    [TestMethod]
    public void IsCommentaryStream_DetectsCommentaryCorrectly()
    {
        var commentaryStream = new MediaStream
        {
            Type = MediaStreamType.Audio,
            Title = "Director's Commentary"
        };
        var polishCommentaryStream = new MediaStream
        {
            Type = MediaStreamType.Audio,
            Title = "Komentarz reżysera"
        };
        var descriptionStream = new MediaStream
        {
            Type = MediaStreamType.Audio,
            Title = "Audio Description for the Visually Impaired"
        };
        var normalStream = new MediaStream
        {
            Type = MediaStreamType.Audio,
            Title = "English Surround"
        };

        Assert.IsTrue(QualityDetectionService.IsCommentaryStream(commentaryStream));
        Assert.IsTrue(QualityDetectionService.IsCommentaryStream(polishCommentaryStream));
        Assert.IsTrue(QualityDetectionService.IsCommentaryStream(descriptionStream));
        Assert.IsFalse(QualityDetectionService.IsCommentaryStream(normalStream));
    }

    [TestMethod]
    [DataRow("eng", "en")]
    [DataRow("fre", "fr")]
    [DataRow("fra", "fr")]
    [DataRow("pol", "pl")]
    [DataRow("deu", "de")]
    [DataRow("en-US", "en")]
    [DataRow("pl_PL", "pl")]
    [DataRow("", "")]
    [DataRow(null, "")]
    public void NormalizeLanguageCode_NormalizesCorrectly(string? input, string expected)
    {
        var result = QualityDetectionService.NormalizeLanguageCode(input);
        Assert.AreEqual(expected, result);
    }

    [TestMethod]
    public void DetectLanguages_FiltersUndAndZxx_AndGeneratesVostSubtitles()
    {
        var streams = new List<MediaStream>
        {
            new() { Type = MediaStreamType.Audio, Language = "eng" },
            new() { Type = MediaStreamType.Audio, Language = "und" },
            new() { Type = MediaStreamType.Subtitle, Language = "pol" },
            new() { Type = MediaStreamType.Subtitle, Language = "zxx" },
            new() { Type = MediaStreamType.Subtitle, Language = "eng" } // Same as audio, should NOT get VOST
        };

        var badges = QualityDetectionService.DetectLanguages(streams);

        // English audio should be present
        Assert.IsTrue(badges.Any(b => b.Category == BadgeCategory.Language && b.BadgeKey == "en"));
        // "und" audio should NOT be present
        Assert.IsFalse(badges.Any(b => b.BadgeKey == "und"));
        // "pol" VOST subtitle should be present
        Assert.IsTrue(badges.Any(b => b.Category == BadgeCategory.Subtitle && b.BadgeKey == "vostpl"));
        // "zxx" subtitle should NOT be present
        Assert.IsFalse(badges.Any(b => b.BadgeKey == "vostzxx"));
        // "eng" subtitle should NOT get VOST because English audio is already present
        Assert.IsFalse(badges.Any(b => b.BadgeKey == "vosten"));
    }

    [TestMethod]
    public void DeduplicateBadges_PreservesFirstHighestPriorityBadge()
    {
        var badges = new List<BadgeInfo>
        {
            new() { Category = BadgeCategory.Hdr, BadgeKey = "dv" },
            new() { Category = BadgeCategory.Hdr, BadgeKey = "hdr10" },
            new() { Category = BadgeCategory.Hdr, BadgeKey = "dv" } // duplicate
        };

        QualityDetectionService.DeduplicateBadges(badges);

        Assert.HasCount(2, badges);
        Assert.AreEqual("dv", badges[0].BadgeKey);
        Assert.AreEqual("hdr10", badges[1].BadgeKey);
    }
}
