using Jellyfin.Plugin.JellyTag.Configuration;
using Jellyfin.Plugin.JellyTag.Middleware;
using Jellyfin.Plugin.JellyTag.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jellyfin.Plugin.JellyTag.Tests;

[TestClass]
public class ConfigurationAndCacheTests
{
    [TestMethod]
    public void ComputeConfigFingerprint_ChangesWhenThumbnailReductionChanges()
    {
        var config1 = new PluginConfiguration { ThumbnailSizeReduction = 0 };
        var config2 = new PluginConfiguration { ThumbnailSizeReduction = 20 };

        var fp1 = ImageCacheService.ComputeConfigFingerprint(config1);
        var fp2 = ImageCacheService.ComputeConfigFingerprint(config2);

        Assert.AreNotEqual(fp1, fp2, "Fingerprint should differ when ThumbnailSizeReduction changes");
    }

    [TestMethod]
    public void ComputeConfigFingerprint_ChangesWhenClientSettingsChange()
    {
        var config1 = new PluginConfiguration { HideDolbyVisionOnSamsungClients = false };
        var config2 = new PluginConfiguration { HideDolbyVisionOnSamsungClients = true };

        var fp1 = ImageCacheService.ComputeConfigFingerprint(config1);
        var fp2 = ImageCacheService.ComputeConfigFingerprint(config2);

        Assert.AreNotEqual(fp1, fp2, "Fingerprint should differ when client filtering changes");
    }

    [TestMethod]
    public void ClonePanelWithReduction_PreservesIconStyleAndProperties()
    {
        var original = new BadgePanelSettings
        {
            Enabled = true,
            SizePercent = 50,
            Position = BadgePosition.TopRight,
            Style = BadgeStyle.Image,
            IconStyle = BadgeIconStyle.Round,
            EnabledBadges = ["4k", "1080p"]
        };

        var reduced = ImageOverlayMiddleware.ClonePanelWithReduction(original, 15);

        Assert.AreEqual(35, reduced.SizePercent, "SizePercent should be reduced by 15");
        Assert.AreEqual(BadgeIconStyle.Round, reduced.IconStyle, "IconStyle should be preserved (not reset to Rectangular)");
        Assert.AreEqual(BadgePosition.TopRight, reduced.Position);
        Assert.AreEqual(BadgeStyle.Image, reduced.Style);
        CollectionAssert.AreEqual(new[] { "4k", "1080p" }, reduced.EnabledBadges);
    }

    [TestMethod]
    public void BackfillNewBadges_AddsMissingAudioAndVideoCodecsToExistingPanels()
    {
        var config = new PluginConfiguration();
        // Simulate an older installation that only had legacy badges
        config.PosterConfig.AudioPanel.EnabledBadges = ["atmos", "truehd", "dtshdma"];
        config.PosterConfig.CodecPanel.EnabledBadges = ["h264", "hevc", "av1", "vp9"];
        config.PosterConfig.HdrPanel.EnabledBadges = ["dv", "hdr10", "hdr10plus", "hlg"];

        config.BackfillNewBadges();

        var audioBadges = config.PosterConfig.AudioPanel.EnabledBadges;
        CollectionAssert.Contains(audioBadges, "flac");
        CollectionAssert.Contains(audioBadges, "dts");
        CollectionAssert.Contains(audioBadges, "eac3");
        CollectionAssert.Contains(audioBadges, "ac3");
        CollectionAssert.Contains(audioBadges, "aac");

        var codecBadges = config.PosterConfig.CodecPanel.EnabledBadges;
        CollectionAssert.Contains(codecBadges, "mpeg2");
        CollectionAssert.Contains(codecBadges, "vc1");

        var hdrBadges = config.PosterConfig.HdrPanel.EnabledBadges;
        CollectionAssert.Contains(hdrBadges, "hdr");
    }

    [TestMethod]
    public void DefaultConfigs_IncludeAllNewAudioAndCodecBadges()
    {
        var poster = PluginConfiguration.CreateDefaultPosterConfig();
        var thumb = PluginConfiguration.CreateDefaultThumbnailConfig();

        foreach (var c in new[] { poster, thumb })
        {
            CollectionAssert.Contains(c.AudioPanel.EnabledBadges, "flac");
            CollectionAssert.Contains(c.AudioPanel.EnabledBadges, "dts");
            CollectionAssert.Contains(c.AudioPanel.EnabledBadges, "eac3");
            CollectionAssert.Contains(c.AudioPanel.EnabledBadges, "ac3");
            CollectionAssert.Contains(c.AudioPanel.EnabledBadges, "aac");

            CollectionAssert.Contains(c.CodecPanel.EnabledBadges, "mpeg2");
            CollectionAssert.Contains(c.CodecPanel.EnabledBadges, "vc1");

            CollectionAssert.Contains(c.HdrPanel.EnabledBadges, "hdr");
        }
    }
}
