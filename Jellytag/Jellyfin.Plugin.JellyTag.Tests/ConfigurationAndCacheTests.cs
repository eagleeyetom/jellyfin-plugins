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
    public void ComputeConfigFingerprint_ChangesWhenFireTvSettingsChange()
    {
        var config1 = new PluginConfiguration { HideDolbyVisionOnFireTvClients = false };
        var config2 = new PluginConfiguration { HideDolbyVisionOnFireTvClients = true };

        var fp1 = ImageCacheService.ComputeConfigFingerprint(config1);
        var fp2 = ImageCacheService.ComputeConfigFingerprint(config2);

        Assert.AreNotEqual(fp1, fp2, "Fingerprint should differ when Fire TV client filtering changes");
    }

    [TestMethod]
    public void ComputeConfigFingerprint_ChangesWhenWhiteLogoBackgroundChanges()
    {
        var config1 = new PluginConfiguration { WhiteLogoBackground = false };
        var config2 = new PluginConfiguration { WhiteLogoBackground = true };

        var fp1 = ImageCacheService.ComputeConfigFingerprint(config1);
        var fp2 = ImageCacheService.ComputeConfigFingerprint(config2);

        Assert.AreNotEqual(fp1, fp2, "Fingerprint should differ when WhiteLogoBackground changes");
    }

    [TestMethod]
    public void ComputeConfigFingerprint_ChangesWhenUseOriginalAacLogoChanges()
    {
        var config1 = new PluginConfiguration { UseOriginalAacLogo = false };
        var config2 = new PluginConfiguration { UseOriginalAacLogo = true };

        var fp1 = ImageCacheService.ComputeConfigFingerprint(config1);
        var fp2 = ImageCacheService.ComputeConfigFingerprint(config2);

        Assert.AreNotEqual(fp1, fp2, "Fingerprint should differ when UseOriginalAacLogo changes");
    }

    [TestMethod]
    public void ComputeConfigFingerprint_ChangesWhenPanelWhiteLogoBackgroundChanges()
    {
        var config1 = new PluginConfiguration();
        config1.PosterConfig.AudioPanel.WhiteLogoBackground = null;
        var config2 = new PluginConfiguration();
        config2.PosterConfig.AudioPanel.WhiteLogoBackground = true;

        var fp1 = ImageCacheService.ComputeConfigFingerprint(config1);
        var fp2 = ImageCacheService.ComputeConfigFingerprint(config2);

        Assert.AreNotEqual(fp1, fp2, "Fingerprint should differ when panel WhiteLogoBackground override changes");
    }

    [TestMethod]
    public void IsFireTvClient_IdentifiesFireTvHeaders()
    {
        var context1 = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context1.Request.Headers["X-Emby-Device-Name"] = "Fire TV Stick 4K";
        Assert.IsTrue(ImageOverlayMiddleware.IsFireTvClient(context1));

        var context2 = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context2.Request.Headers["X-Emby-Device-Name"] = "AFTMM";
        Assert.IsTrue(ImageOverlayMiddleware.IsFireTvClient(context2));

        var context3 = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context3.Request.Headers["X-Emby-Client"] = "Fire TV Client";
        Assert.IsTrue(ImageOverlayMiddleware.IsFireTvClient(context3));

        var context4 = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context4.Request.Headers["User-Agent"] = "Mozilla/5.0 (Linux; Android 11; AFTSO Build/...)";
        Assert.IsTrue(ImageOverlayMiddleware.IsFireTvClient(context4));

        var context5 = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context5.Request.Headers["X-Emby-Device-Name"] = "Nvidia Shield";
        context5.Request.Headers["X-Emby-Client"] = "Jellyfin for Android TV";
        Assert.IsFalse(ImageOverlayMiddleware.IsFireTvClient(context5));
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
            EnabledBadges = ["4k", "1080p"],
            DisabledLogos = ["ac3", "aac"]
        };

        var reduced = ImageOverlayMiddleware.ClonePanelWithReduction(original, 15);

        Assert.AreEqual(35, reduced.SizePercent, "SizePercent should be reduced by 15");
        Assert.AreEqual(BadgeIconStyle.Round, reduced.IconStyle, "IconStyle should be preserved (not reset to Rectangular)");
        Assert.AreEqual(BadgePosition.TopRight, reduced.Position);
        Assert.AreEqual(BadgeStyle.Image, reduced.Style);
        CollectionAssert.AreEqual(new[] { "4k", "1080p" }, reduced.EnabledBadges);
        CollectionAssert.AreEqual(new[] { "ac3", "aac" }, reduced.DisabledLogos);
    }

    [TestMethod]
    public void ComputeConfigFingerprint_ChangesWhenDisabledLogosChange()
    {
        var config1 = new PluginConfiguration();
        var config2 = new PluginConfiguration();
        config2.PosterConfig.AudioPanel.DisabledLogos.Add("ac3");

        var fp1 = ImageCacheService.ComputeConfigFingerprint(config1);
        var fp2 = ImageCacheService.ComputeConfigFingerprint(config2);

        Assert.AreNotEqual(fp1, fp2, "Fingerprint should differ when DisabledLogos change");
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

    [TestMethod]
    [DataRow("logo-flac.svg")]
    [DataRow("logo-dts.svg")]
    [DataRow("logo-ac3.svg")]
    [DataRow("logo-eac3.svg")]
    [DataRow("logo-aac.svg")]
    [DataRow("logo-mpeg2.svg")]
    public void EmbeddedBrandLogos_ExistAndAreValidXml(string logoFileName)
    {
        var asm = typeof(PluginConfiguration).Assembly;
        var resourceNames = asm.GetManifestResourceNames();
        var match = resourceNames.FirstOrDefault(r => r.EndsWith($".{logoFileName}", StringComparison.OrdinalIgnoreCase));

        Assert.IsNotNull(match, $"Resource {logoFileName} should be embedded in assembly");

        using var stream = asm.GetManifestResourceStream(match);
        Assert.IsNotNull(stream);
#pragma warning disable MSTEST0037
        Assert.IsTrue(stream.Length > 0);
#pragma warning restore MSTEST0037

        var doc = System.Xml.Linq.XDocument.Load(stream);
        Assert.IsNotNull(doc.Root);
        Assert.AreEqual("svg", doc.Root.Name.LocalName);

        using var skSvg = new Svg.Skia.SKSvg();
        using var stream2 = asm.GetManifestResourceStream(match);
        skSvg.Load(stream2);
        Assert.IsNotNull(skSvg.Picture);
        Assert.IsTrue(skSvg.Picture.CullRect.Width > 0, "Width should be > 0");
        Assert.IsTrue(skSvg.Picture.CullRect.Height > 0, "Height should be > 0");
    }

    [TestMethod]
    public async Task ImageOverlayService_WithDisabledLogos_RunsWithoutError()
    {
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<ImageOverlayService>.Instance;
        using var service = new ImageOverlayService(logger);

        var config = new ImageTypeConfig();
        config.AudioPanel.Style = BadgeStyle.Logo;
        config.AudioPanel.DisabledLogos.Add("ac3");

        var badges = new List<BadgeInfo>
        {
            new() { Category = BadgeCategory.Audio, BadgeKey = "ac3", ResourceFileName = "badge-ac3.svg" },
            new() { Category = BadgeCategory.Audio, BadgeKey = "dts", ResourceFileName = "badge-dts.svg" }
        };

        using var bitmap = new SkiaSharp.SKBitmap(200, 300);
        using var canvas = new SkiaSharp.SKCanvas(bitmap);
        canvas.Clear(SkiaSharp.SKColors.Blue);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 80);
        using var ms = new MemoryStream(data.ToArray());

        var (resultStream, contentType) = await service.AddBadgeOverlaysAsync(ms, badges, config);
        Assert.IsNotNull(resultStream);
        Assert.IsTrue(resultStream.Length > 0);
        resultStream.Dispose();
    }

    [TestMethod]
    public void ChannelPanel_DefaultConfigs_IncludeAllChannelBadges()
    {
        var poster = PluginConfiguration.CreateDefaultPosterConfig();
        var thumb = PluginConfiguration.CreateDefaultThumbnailConfig();

        foreach (var c in new[] { poster, thumb })
        {
            Assert.IsNotNull(c.ChannelPanel);
            Assert.IsTrue(c.ChannelPanel.Enabled);
            Assert.AreEqual(4, c.ChannelPanel.Order);
            Assert.AreEqual(5, c.LanguagePanel.Order);

            CollectionAssert.Contains(c.ChannelPanel.EnabledBadges, "7.1");
            CollectionAssert.Contains(c.ChannelPanel.EnabledBadges, "5.1");
            CollectionAssert.Contains(c.ChannelPanel.EnabledBadges, "stereo");
            CollectionAssert.Contains(c.ChannelPanel.EnabledBadges, "mono");

            // AudioPanel should no longer contain channel badges
            CollectionAssert.DoesNotContain(c.AudioPanel.EnabledBadges, "7.1");
            CollectionAssert.DoesNotContain(c.AudioPanel.EnabledBadges, "5.1");
            CollectionAssert.DoesNotContain(c.AudioPanel.EnabledBadges, "stereo");
            CollectionAssert.DoesNotContain(c.AudioPanel.EnabledBadges, "mono");
        }
    }

    [TestMethod]
    public void ChannelPanel_FingerprintChangesWhenChannelPanelChanges()
    {
        var config1 = new PluginConfiguration();
        var config2 = new PluginConfiguration();
        config2.PosterConfig.ChannelPanel.SizePercent = 25;

        var fp1 = ImageCacheService.ComputeConfigFingerprint(config1);
        var fp2 = ImageCacheService.ComputeConfigFingerprint(config2);

        Assert.AreNotEqual(fp1, fp2, "Fingerprint should differ when ChannelPanel.SizePercent changes");
    }

    [TestMethod]
    public void ChannelPanel_Backfill_MigratesChannelsFromAudioPanel()
    {
        var config = new PluginConfiguration();
        // Simulate older install with channel badges in AudioPanel and uninitialized ChannelPanel
        config.PosterConfig.AudioPanel.EnabledBadges = ["atmos", "truehd", "ac3", "5.1", "7.1"];
        config.PosterConfig.ChannelPanel = new BadgePanelSettings { EnabledBadges = new List<string>() };

        config.BackfillNewBadges();

        CollectionAssert.Contains(config.PosterConfig.ChannelPanel.EnabledBadges, "5.1");
        CollectionAssert.Contains(config.PosterConfig.ChannelPanel.EnabledBadges, "7.1");
        CollectionAssert.DoesNotContain(config.PosterConfig.AudioPanel.EnabledBadges, "5.1");
        CollectionAssert.DoesNotContain(config.PosterConfig.AudioPanel.EnabledBadges, "7.1");
    }

    [TestMethod]
    public async Task ImageOverlayService_RendersBothAudioLogoAndChannelBadgeWithShowModeHighest()
    {
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<ImageOverlayService>.Instance;
        using var service = new ImageOverlayService(logger);

        var config = new ImageTypeConfig();
        config.AudioPanel.Style = BadgeStyle.Logo;
        config.AudioPanel.ShowMode = BadgeDisplayMode.Highest;
        config.AudioPanel.EnabledBadges = ["ac3"];

        config.ChannelPanel.Style = BadgeStyle.Image;
        config.ChannelPanel.ShowMode = BadgeDisplayMode.Highest;
        config.ChannelPanel.EnabledBadges = ["5.1"];

        var badges = new List<BadgeInfo>
        {
            new() { Category = BadgeCategory.Audio, BadgeKey = "ac3", ResourceFileName = "badge-ac3.svg" },
            new() { Category = BadgeCategory.Channels, BadgeKey = "5.1", ResourceFileName = "badge-5_1.svg" }
        };

        using var bitmap = new SkiaSharp.SKBitmap(300, 450);
        using var canvas = new SkiaSharp.SKCanvas(bitmap);
        canvas.Clear(SkiaSharp.SKColors.DarkGray);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 80);
        using var ms = new MemoryStream(data.ToArray());

        var (resultStream, contentType) = await service.AddBadgeOverlaysAsync(ms, badges, config);
        Assert.IsNotNull(resultStream);
#pragma warning disable MSTEST0037
        Assert.IsTrue(resultStream.Length > 0);
#pragma warning restore MSTEST0037
        resultStream.Dispose();
    }

    [TestMethod]
    public void ComputeConfigFingerprint_WithCustomBadgeColors_ChangesHash()
    {
        var config1 = new PluginConfiguration();
        var hash1 = ImageCacheService.ComputeConfigFingerprint(config1);

        var config2 = new PluginConfiguration();
        config2.CustomBadgeColors.Add(new BadgeTypeStyleOverride
        {
            BadgeKey = "hevc",
            BgColor = "#112233",
            TextColor = "#ffffff"
        });
        var hash2 = ImageCacheService.ComputeConfigFingerprint(config2);

        Assert.AreNotEqual(hash1, hash2, "Adding CustomBadgeColors must change the fingerprint hash");

        config2.CustomBadgeColors[0].TextColor = "#ff0000";
        var hash3 = ImageCacheService.ComputeConfigFingerprint(config2);
        Assert.AreNotEqual(hash2, hash3, "Changing TextColor must change the fingerprint hash");
    }

    [TestMethod]
    public void ImageOverlayService_RecolorSvg_RecolorsHevcAnd4K()
    {
        var asm = typeof(ImageOverlayService).Assembly;

        // Test HEVC badge recoloring
        var hevcRes = asm.GetManifestResourceNames().First(r => r.EndsWith("badge-hevc.svg", StringComparison.OrdinalIgnoreCase));
        using var hevcStream = asm.GetManifestResourceStream(hevcRes)!;
        using var hevcMs = new MemoryStream();
        hevcStream.CopyTo(hevcMs);

        var recoloredHevc = ImageOverlayService.RecolorSvg(hevcMs.ToArray(), "#123456", "#ABCDEF", "hevc");
        var hevcXml = System.Text.Encoding.UTF8.GetString(recoloredHevc);
        StringAssert.Contains(hevcXml, "#123456");
        StringAssert.Contains(hevcXml, "#ABCDEF");

        // Test 4K badge recoloring
        var fourKRes = asm.GetManifestResourceNames().First(r => r.EndsWith("badge-4k-white.svg", StringComparison.OrdinalIgnoreCase));
        using var fourKStream = asm.GetManifestResourceStream(fourKRes)!;
        using var fourKMs = new MemoryStream();
        fourKStream.CopyTo(fourKMs);

        var recolored4K = ImageOverlayService.RecolorSvg(fourKMs.ToArray(), "#654321", "#FEDCBA", "4k");
        var fourKXml = System.Text.Encoding.UTF8.GetString(recolored4K);
        StringAssert.Contains(fourKXml, "#654321");
        StringAssert.Contains(fourKXml, "#FEDCBA");
    }

    [TestMethod]
    public void ImageOverlayService_AacLogoAssets_ExistAndHaveCorrectAspectRatios()
    {
        var asm = typeof(ImageOverlayService).Assembly;
        var names = asm.GetManifestResourceNames();

        var standardRes = names.FirstOrDefault(r => r.EndsWith("logo-aac.svg", StringComparison.OrdinalIgnoreCase));
        var standardWhiteRes = names.FirstOrDefault(r => r.EndsWith("logo-aac-white.svg", StringComparison.OrdinalIgnoreCase));
        var origRes = names.FirstOrDefault(r => r.EndsWith("logo-aac-orig.svg", StringComparison.OrdinalIgnoreCase));
        var origWhiteRes = names.FirstOrDefault(r => r.EndsWith("logo-aac-orig-white.svg", StringComparison.OrdinalIgnoreCase));

        Assert.IsNotNull(standardRes, "logo-aac.svg resource should exist");
        Assert.IsNotNull(standardWhiteRes, "logo-aac-white.svg resource should exist");
        Assert.IsNotNull(origRes, "logo-aac-orig.svg resource should exist");
        Assert.IsNotNull(origWhiteRes, "logo-aac-orig-white.svg resource should exist");

        // Verify standard logo is wide rectangular (1140x540)
        using var stdStream = asm.GetManifestResourceStream(standardRes)!;
        using var stdMs = new MemoryStream();
        stdStream.CopyTo(stdMs);
        var stdXml = System.Text.Encoding.UTF8.GetString(stdMs.ToArray());
        StringAssert.Contains(stdXml, "width=\"1140\"");
        StringAssert.Contains(stdXml, "height=\"540\"");

        // Verify original logo is tall square-ish (1040x1138)
        using var origStream = asm.GetManifestResourceStream(origRes)!;
        using var origMs = new MemoryStream();
        origStream.CopyTo(origMs);
        var origXml = System.Text.Encoding.UTF8.GetString(origMs.ToArray());
        StringAssert.Contains(origXml, "width=\"1040\"");
        StringAssert.Contains(origXml, "height=\"1138\"");
    }

    [TestMethod]
    public void ClonePanelWithReduction_PreservesWhiteLogoBackground()
    {
        var panel = new BadgePanelSettings
        {
            WhiteLogoBackground = true,
            SizePercent = 20
        };

        var cloned = Jellyfin.Plugin.JellyTag.Middleware.ImageOverlayMiddleware.ClonePanelWithReduction(panel, 5);

        Assert.IsTrue(cloned.WhiteLogoBackground);
        Assert.AreEqual(15, cloned.SizePercent);
    }

    [TestMethod]
    public void DetectImageContentType_RecognizesFormats()
    {
        // JPEG
        var jpegBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01 };
        using (var ms = new MemoryStream(jpegBytes))
        {
            Assert.AreEqual("image/jpeg", ImageOverlayService.DetectImageContentType(ms));
        }

        // PNG
        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        using (var ms = new MemoryStream(pngBytes))
        {
            Assert.AreEqual("image/png", ImageOverlayService.DetectImageContentType(ms));
        }

        // WebP
        var webpBytes = new byte[] { 0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50 };
        using (var ms = new MemoryStream(webpBytes))
        {
            Assert.AreEqual("image/webp", ImageOverlayService.DetectImageContentType(ms));
        }
    }
}
