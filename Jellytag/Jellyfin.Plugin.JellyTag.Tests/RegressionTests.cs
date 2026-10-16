using System.Text.Json;
using Jellyfin.Plugin.JellyTag.Configuration;
using Jellyfin.Plugin.JellyTag.Controllers;
using Jellyfin.Plugin.JellyTag.Middleware;
using Jellyfin.Plugin.JellyTag.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkiaSharp;

namespace Jellyfin.Plugin.JellyTag.Tests;

[TestClass]
public class RegressionTests
{
    // ----- Imported configuration with null sections -----

    [TestMethod]
    public void EnsureValid_ImportedJsonWithNullSections_IsUsableByImagePipeline()
    {
        const string json = """
            {
              "Enabled": true,
              "ExcludedLibraryIds": null,
              "PosterConfig": null,
              "ThumbnailConfig": { "AudioPanel": null, "HdrPanel": { "EnabledBadges": null, "BadgeTypeOverrides": null } },
              "CustomBadgeTexts": [ null ],
              "CustomBadgeColors": null,
              "LanguageFlagOverrides": null
            }
            """;

        var config = JsonSerializer.Deserialize<PluginConfiguration>(json, JellyTagController.JsonOptions)!;

        config.EnsureValid();

        Assert.IsNotNull(config.ExcludedLibraryIds);
        Assert.IsNotNull(config.PosterConfig);
        Assert.IsNotNull(config.ThumbnailConfig.AudioPanel);
        Assert.IsNotNull(config.ThumbnailConfig.HdrPanel.EnabledBadges);
        Assert.IsNotNull(config.ThumbnailConfig.HdrPanel.BadgeTypeOverrides);
        Assert.IsEmpty(config.CustomBadgeTexts);
        Assert.IsNotNull(config.CustomBadgeColors);
        Assert.IsNotNull(config.LanguageFlagOverrides);

        // Everything a poster request touches must work on the normalized configuration.
        Assert.IsFalse(string.IsNullOrEmpty(ImageCacheService.ComputeConfigFingerprint(config)));
        var posterConfig = ImageOverlayMiddleware.GetImageTypeConfig(config, "Primary", new Movie());
        Assert.IsNotNull(posterConfig);
        Assert.IsNotNull(ImageOverlayMiddleware.ApplySizeReduction(config.ThumbnailConfig, 5));
    }

    [TestMethod]
    public void EnsureValid_KeepsValidImportedValues()
    {
        var config = new PluginConfiguration { ExcludedLibraryIds = ["abc", " ", null!], JpegQuality = 70 };
        config.PosterConfig.ResolutionPanel.SizePercent = 33;

        config.EnsureValid();

        CollectionAssert.AreEqual(new[] { "abc" }, config.ExcludedLibraryIds);
        Assert.AreEqual(70, config.JpegQuality);
        Assert.AreEqual(33, config.PosterConfig.ResolutionPanel.SizePercent);
    }

    [TestMethod]
    public void JsonOptions_AcceptNumbersSentAsStrings()
    {
        var config = JsonSerializer.Deserialize<PluginConfiguration>("""{ "JpegQuality": "75" }""", JellyTagController.JsonOptions)!;

        Assert.AreEqual(75, config.JpegQuality);
    }

    // ----- ReloadBadges while a render is in flight -----

    [TestMethod]
    public async Task ReloadBadges_DoesNotDisposeBitmapsHeldByInFlightRender()
    {
        using var service = new ImageOverlayService(NullLogger<ImageOverlayService>.Instance);
        var panel = new BadgePanelSettings { Style = BadgeStyle.Image, SizePercent = 15 };
        var badges = new List<BadgeInfo>
        {
            new() { Category = BadgeCategory.Resolution, BadgeKey = "4k", ResourceFileName = "badge-4k.svg" },
            new() { Category = BadgeCategory.Hdr, BadgeKey = "hdr10", ResourceFileName = "badge-hdr10.svg" }
        };

        var sizes = new List<SKSizeI>();
        var sourceBitmaps = new List<SKBitmap>();
        var filtered = new List<BadgeInfo>();
        var ownedBitmaps = new List<SKBitmap>();

        try
        {
            await service.PrepareBadgeGroup(badges, panel.SizePercent, 400, false, sizes, sourceBitmaps, filtered, ownedBitmaps, panel);
            Assert.HasCount(2, sourceBitmaps);

            // An admin clears the cache / uploads a badge between prepare and draw.
            service.ReloadBadges();

            using var surface = SKSurface.Create(new SKImageInfo(400, 600));
            foreach (var bitmap in sourceBitmaps)
            {
                Assert.AreNotEqual(IntPtr.Zero, bitmap.Handle, "Bitmap in use by a render was disposed by ReloadBadges");
                using var image = SKImage.FromBitmap(bitmap);
                Assert.IsNotNull(image);
                surface.Canvas.DrawImage(image, 0, 0);
            }

            // The next render loads a fresh generation.
            var nextSizes = new List<SKSizeI>();
            var nextBitmaps = new List<SKBitmap>();
            var nextOwned = new List<SKBitmap>();
            await service.PrepareBadgeGroup(badges, panel.SizePercent, 400, false, nextSizes, nextBitmaps, new List<BadgeInfo>(), nextOwned, panel);
            Assert.HasCount(2, nextBitmaps);
            foreach (var b in nextOwned) b.Dispose();
        }
        finally
        {
            foreach (var b in ownedBitmaps) b.Dispose();
        }
    }

    // ----- Cache writes for the same image at the same time -----

    [TestMethod]
    public async Task CacheImageAsync_WhenAnotherWriterHoldsTheOldTempName_StillCaches()
    {
        var dir = CreateTempDir();
        try
        {
            var service = new ImageCacheService(NullLogger<ImageCacheService>.Instance, dir);
            var itemId = Guid.NewGuid();
            var cachePath = service.GetCacheFilePath(itemId, "4k", "tag");

            // Simulates a concurrent request that is mid-write using the previous fixed temp name.
            using (new FileStream(cachePath + ".tmp", FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await service.CacheImageAsync(itemId, "4k", "tag", new MemoryStream(new byte[] { 1, 2, 3 }));
            }

            await using var cached = await service.GetCachedImageAsync(itemId, "4k", "tag");
            Assert.IsNotNull(cached, "Image should be cached even while another write is in progress");
            Assert.AreEqual(3, cached.Length);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public async Task CacheImageAsync_ConcurrentWritesOfSameImage_LeaveOneCompleteFileAndNoTempFiles()
    {
        var dir = CreateTempDir();
        try
        {
            var service = new ImageCacheService(NullLogger<ImageCacheService>.Instance, dir);
            var itemId = Guid.NewGuid();
            var payload = Enumerable.Range(0, 256 * 1024).Select(i => (byte)i).ToArray();

            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
                Task.Run(() => service.CacheImageAsync(itemId, "4k", "tag", new MemoryStream(payload)))));

            var cachePath = service.GetCacheFilePath(itemId, "4k", "tag");
            CollectionAssert.AreEqual(payload, File.ReadAllBytes(cachePath));
            Assert.IsEmpty(Directory.GetFiles(dir, "*" + ImageCacheService.TempFileExtension));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ----- Live preview renders unsaved settings without persisting them -----

    [TestMethod]
    public async Task RenderPreviewAsync_RendersBadgesFromRequestConfiguration()
    {
        using var overlay = new ImageOverlayService(NullLogger<ImageOverlayService>.Instance);
        var quality = new FixedBadges(new BadgeInfo { Category = BadgeCategory.Resolution, BadgeKey = "4k", ResourceFileName = "badge-4k.svg" });
        var config = new PluginConfiguration { Enabled = true };
        var source = CreateJpeg(300, 450);

        var (bytes, contentType) = await JellyTagController.RenderPreviewAsync(new Movie(), "Primary", config, new MemoryStream(source), quality, overlay);

        Assert.AreEqual("image/jpeg", contentType);
        CollectionAssert.AreNotEqual(source, bytes, "The preview should carry the badge from the posted configuration");
        Assert.IsFalse(PluginConfigurationScope.IsOverridden, "The preview configuration must not leak past the render");
        Assert.IsFalse(overlay.ShouldShowBadge(quality.Badges[0], config.PosterConfig), "Outside the preview the saved configuration applies");
    }

    [TestMethod]
    public async Task RenderPreviewAsync_DisabledInRequestConfiguration_ReturnsSourceImage()
    {
        using var overlay = new ImageOverlayService(NullLogger<ImageOverlayService>.Instance);
        var quality = new FixedBadges(new BadgeInfo { Category = BadgeCategory.Resolution, BadgeKey = "4k", ResourceFileName = "badge-4k.svg" });
        var config = new PluginConfiguration { Enabled = false };
        var source = CreateJpeg(300, 450);

        var (bytes, _) = await JellyTagController.RenderPreviewAsync(new Movie(), "Primary", config, new MemoryStream(source), quality, overlay);

        CollectionAssert.AreEqual(source, bytes);
    }

    // ----- jellyfin-web list view (season episode list) crops thumbnails to 3:2 / 11:8 -----

    [TestMethod]
    [DataRow("?fillWidth=500&fillHeight=500&quality=96&tag=abc", ImageDisplayContext.ListView)]
    [DataRow("?FillWidth=500&FillHeight=500", ImageDisplayContext.ListView)]
    [DataRow("?fillWidth=400&fillHeight=225&quality=96", ImageDisplayContext.Card)]
    [DataRow("?fillWidth=500", ImageDisplayContext.Card)]
    [DataRow("?maxWidth=500&maxHeight=500", ImageDisplayContext.Card)]
    [DataRow("", ImageDisplayContext.Card)]
    public void GetDisplayContext_RecognizesListViewSquareFillBox(string queryString, ImageDisplayContext expected)
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString(queryString);

        Assert.AreEqual(expected, ImageOverlayMiddleware.GetDisplayContext(context.Request.Query));
    }

    [TestMethod]
    public void CalculateSafeAreaCrop_ListView_KeepsLandscapeBadgesInsideNarrowestFrame()
    {
        // 16:9 in an 11:8 frame (narrow screens) loses the most: 1920 - round(1080 * 1.375) = 435 -> 217 per side.
        var (cropX, cropY) = ImageOverlayService.CalculateSafeAreaCrop(1920, 1080, ImageDisplayContext.ListView);

        Assert.AreEqual(217, cropX);
        Assert.AreEqual(0, cropY);

        // Cards keep the previous behaviour: a 16:9 thumbnail is fully visible.
        Assert.AreEqual((0, 0), ImageOverlayService.CalculateSafeAreaCrop(1920, 1080, ImageDisplayContext.Card));
    }

    [TestMethod]
    public void CalculateSafeAreaCrop_ListView_PortraitFallbackPosterIsCroppedVertically()
    {
        // Episodes without their own image fall back to the series poster, still shown in the 3:2 frame.
        var (cropX, cropY) = ImageOverlayService.CalculateSafeAreaCrop(1000, 1500, ImageDisplayContext.ListView);

        Assert.AreEqual(0, cropX);
        Assert.AreEqual(416, cropY); // (1500 - round(1000 / 1.5)) / 2
    }

    [TestMethod]
    [DataRow(1920, 1080, 1.5f, 150, 0)]
    [DataRow(1920, 1080, 16f / 9f, 0, 0)]
    [DataRow(1000, 1426, 2f / 3f, 24, 0)]
    [DataRow(800, 600, 16f / 9f, 0, 75)]
    public void CalculateCoverCrop_MatchesCssBackgroundSizeCover(int width, int height, float frameRatio, int expectedX, int expectedY)
    {
        Assert.AreEqual((expectedX, expectedY), ImageOverlayService.CalculateCoverCrop(width, height, frameRatio));
    }

    [TestMethod]
    public async Task AddBadgeOverlaysAsync_ListView_LeavesCroppedLeftEdgeUntouched()
    {
        using var overlay = new ImageOverlayService(NullLogger<ImageOverlayService>.Instance);
        var imageConfig = new ImageTypeConfig();
        imageConfig.ResolutionPanel.Enabled = true;
        imageConfig.ResolutionPanel.Position = BadgePosition.TopLeft;
        imageConfig.ResolutionPanel.Style = BadgeStyle.Text;
        imageConfig.ResolutionPanel.TextBgOpacity = 255;
        imageConfig.ResolutionPanel.EnabledBadges = ["4k"];
        var badges = new List<BadgeInfo> { new() { Category = BadgeCategory.Resolution, BadgeKey = "4k", ResourceFileName = "badge-4k.svg" } };

        var source = CreateJpeg(1920, 1080);
        var (resultStream, _) = await overlay.AddBadgeOverlaysAsync(new MemoryStream(source), badges, imageConfig, ImageDisplayContext.ListView);
        await using (resultStream)
        {
            using var result = SKBitmap.Decode(resultStream);
            var background = SKColors.DarkSlateBlue;

            // Everything left of the 217 px that the list view hides must stay as it was.
            for (var y = 0; y < 400; y += 4)
            {
                for (var x = 0; x < 210; x += 4)
                {
                    Assert.IsLessThan(30, ColorDistance(result.GetPixel(x, y), background), $"Pixel ({x},{y}) in the hidden area was drawn on");
                }
            }

            // The badge starts at the visible edge plus the margin (2.5% of the visible width).
            var badgeLeft = 217 + (int)((1920 - (2 * 217)) * 0.025f);
            Assert.IsGreaterThan(60, ColorDistance(result.GetPixel(badgeLeft + 40, 80), background), "Badge should be drawn inside the visible list-view frame");
        }
    }

    [TestMethod]
    [DataRow(ImageDisplayContext.Card, 75, 1920)]
    [DataRow(ImageDisplayContext.ListView, 100, 1486)]
    [DataRow(ImageDisplayContext.ListView, 75, 1114)]
    [DataRow(ImageDisplayContext.ListView, 10, 372)]
    [DataRow(ImageDisplayContext.ListView, 500, 1486)]
    public void GetBadgeReferenceWidth_ListViewUsesVisibleFrameAndScale(ImageDisplayContext context, int scalePercent, int expected)
    {
        // 1920 px wide 16:9 thumbnail, of which 217 px per side are hidden in the list view.
        Assert.AreEqual(expected, ImageOverlayService.GetBadgeReferenceWidth(1920, 217, context, scalePercent));
    }

    [TestMethod]
    public async Task AddBadgeOverlaysAsync_ListView_DrawsSmallerBadgeThanCard()
    {
        using var overlay = new ImageOverlayService(NullLogger<ImageOverlayService>.Instance);
        var imageConfig = new ImageTypeConfig();
        imageConfig.ResolutionPanel.Enabled = true;
        imageConfig.ResolutionPanel.Position = BadgePosition.TopLeft;
        imageConfig.ResolutionPanel.Style = BadgeStyle.Text;
        imageConfig.ResolutionPanel.TextBgOpacity = 255;
        imageConfig.ResolutionPanel.TextCornerRadius = 0;
        imageConfig.ResolutionPanel.SizePercent = 15;
        var badges = new List<BadgeInfo> { new() { Category = BadgeCategory.Resolution, BadgeKey = "4k", ResourceFileName = "badge-4k.svg" } };
        var source = CreateJpeg(1920, 1080);

        // Card: 15% of 1920 = 288 px. List view (default 75%): 15% of 0.75 * 1486 visible px = 167 px.
        var cardWidth = await MeasureTopLeftBadgeWidth(overlay, source, badges, imageConfig, ImageDisplayContext.Card, badgeTop: 48, badgeHeight: 144);
        var listWidth = await MeasureTopLeftBadgeWidth(overlay, source, badges, imageConfig, ImageDisplayContext.ListView, badgeTop: 37, badgeHeight: 83);

        Assert.IsLessThanOrEqualTo(3, Math.Abs(cardWidth - 288), $"Card badge width {cardWidth}");
        Assert.IsLessThanOrEqualTo(3, Math.Abs(listWidth - 167), $"List view badge width {listWidth}");
    }

    private static async Task<int> MeasureTopLeftBadgeWidth(
        ImageOverlayService overlay, byte[] source, List<BadgeInfo> badges, ImageTypeConfig imageConfig,
        ImageDisplayContext context, int badgeTop, int badgeHeight)
    {
        var (resultStream, _) = await overlay.AddBadgeOverlaysAsync(new MemoryStream(source), badges, imageConfig, context);
        await using (resultStream)
        {
            using var result = SKBitmap.Decode(resultStream);

            // Scan just below the top edge, where the plate is solid and text does not reach.
            var y = badgeTop + Math.Max(2, badgeHeight / 12);
            int first = -1, last = -1;
            for (var x = 0; x < result.Width / 2; x++)
            {
                if (ColorDistance(result.GetPixel(x, y), SKColors.DarkSlateBlue) > 60)
                {
                    if (first < 0) first = x;
                    last = x;
                }
            }

            return first < 0 ? 0 : last - first + 1;
        }
    }

    private static int ColorDistance(SKColor a, SKColor b)
        => Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue);

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "JellyTagTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static byte[] CreateJpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.DarkSlateBlue);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }

    private sealed class FixedBadges : IQualityDetectionService
    {
        public FixedBadges(params BadgeInfo[] badges)
        {
            Badges = badges.ToList();
        }

        public List<BadgeInfo> Badges { get; }

        public List<BadgeInfo> DetectAllBadges(BaseItem item) => new(Badges);

        public void ClearBadgeCache()
        {
        }
    }
}
