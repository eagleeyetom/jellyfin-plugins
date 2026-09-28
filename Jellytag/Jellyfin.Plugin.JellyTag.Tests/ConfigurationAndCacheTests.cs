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
}
