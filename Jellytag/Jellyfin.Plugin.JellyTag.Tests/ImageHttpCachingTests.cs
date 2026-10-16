using System.Reflection;
using Jellyfin.Plugin.JellyTag.Configuration;
using Jellyfin.Plugin.JellyTag.Middleware;
using Jellyfin.Plugin.JellyTag.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkiaSharp;

namespace Jellyfin.Plugin.JellyTag.Tests;

[TestClass]
public class ImageHttpCachingTests
{
    private static readonly BadgeInfo Badge4K = new() { Category = BadgeCategory.Resolution, BadgeKey = "4k", ResourceFileName = "badge-4k.svg" };
    private static readonly BadgeInfo BadgeHdr10 = new() { Category = BadgeCategory.Hdr, BadgeKey = "hdr10", ResourceFileName = "badge-hdr10.svg" };

    // ----- ETag / 304 -----

    [TestMethod]
    public async Task InvokeAsync_IfNoneMatchMatchesETag_Returns304WithoutCallingNextOrOverlay()
    {
        var env = new Env();
        using var scope = PluginConfigurationScope.Override(env.Config);

        var first = await env.SendAsync();
        Assert.AreEqual(200, first.Response.StatusCode);
        var etag = first.Response.Headers.ETag.ToString();
        Assert.IsTrue(etag.StartsWith('"') && etag.EndsWith('"'), $"Expected a strong ETag, got {etag}");
        Assert.AreEqual(ImageOverlayMiddleware.BadgedImageCacheControl, first.Response.Headers.CacheControl.ToString());
        StringAssert.Contains(first.Response.Headers.Vary.ToString(), "Accept");
        Assert.AreEqual(1, env.NextCalls);
        Assert.AreEqual(1, env.Overlay.Calls);

        foreach (var ifNoneMatch in new[] { etag, $"\"other\", {etag}", $"W/{etag}" })
        {
            var revalidation = await env.SendAsync(ctx => ctx.Request.Headers.IfNoneMatch = ifNoneMatch);

            Assert.AreEqual(304, revalidation.Response.StatusCode, $"If-None-Match: {ifNoneMatch}");
            Assert.AreEqual(etag, revalidation.Response.Headers.ETag.ToString());
            Assert.AreEqual(ImageOverlayMiddleware.BadgedImageCacheControl, revalidation.Response.Headers.CacheControl.ToString());
            Assert.IsEmpty(revalidation.Body);
        }

        Assert.AreEqual(1, env.NextCalls, "A 304 must not make Jellyfin read the original image");
        Assert.AreEqual(1, env.Overlay.Calls, "A 304 must not draw the badges");
    }

    [TestMethod]
    public async Task InvokeAsync_StaleIfNoneMatch_ServesFullImageWithoutForwardingValidators()
    {
        var env = new Env();
        using var scope = PluginConfigurationScope.Override(env.Config);
        string? forwardedIfNoneMatch = null;
        env.OnNext = ctx => forwardedIfNoneMatch = ctx.Request.Headers.IfNoneMatch.ToString();

        // E.g. Jellyfin's own ETag from before the plugin was installed.
        var result = await env.SendAsync(ctx => ctx.Request.Headers.IfNoneMatch = "\"jellyfin-original\"");

        Assert.AreEqual(200, result.Response.StatusCode);
        Assert.AreNotEqual(0, result.Body.Length);
        Assert.AreEqual(string.Empty, forwardedIfNoneMatch, "Jellyfin must not answer 304 for the unbadged original");
        Assert.AreNotEqual("\"jellyfin-original\"", result.Response.Headers.ETag.ToString());
    }

    [TestMethod]
    public async Task InvokeAsync_ETag_ChangesWithConfigurationAndBadges()
    {
        var env = new Env();
        string baseline;
        using (PluginConfigurationScope.Override(env.Config))
        {
            baseline = (await env.SendAsync()).Response.Headers.ETag.ToString();
            Assert.AreEqual(baseline, (await env.SendAsync()).Response.Headers.ETag.ToString(), "Same inputs must give the same ETag");
        }

        var changedConfig = new PluginConfiguration { Enabled = true, JpegQuality = env.Config.JpegQuality - 10 };
        using (PluginConfigurationScope.Override(changedConfig))
        {
            var afterConfigChange = await env.SendAsync(ctx => ctx.Request.Headers.IfNoneMatch = baseline);

            Assert.AreEqual(200, afterConfigChange.Response.StatusCode, "A configuration change must invalidate the client copy");
            Assert.AreNotEqual(baseline, afterConfigChange.Response.Headers.ETag.ToString());
        }

        using (PluginConfigurationScope.Override(env.Config))
        {
            env.Quality.Badges.Add(BadgeHdr10);
            var afterBadgeChange = await env.SendAsync(ctx => ctx.Request.Headers.IfNoneMatch = baseline);

            Assert.AreEqual(200, afterBadgeChange.Response.StatusCode, "New badges (e.g. a 4K HDR replacement file) must invalidate the client copy");
            Assert.AreNotEqual(baseline, afterBadgeChange.Response.Headers.ETag.ToString());

            env.Quality.Badges.Remove(BadgeHdr10);
            var afterNewImageTag = await env.SendAsync(ctx => ctx.Request.QueryString = new QueryString("?tag=new-image"));
            Assert.AreNotEqual(baseline, afterNewImageTag.Response.Headers.ETag.ToString());
        }
    }

    [TestMethod]
    public void ComputeETag_DependsOnEveryInput()
    {
        var id = Guid.NewGuid();
        var baseline = ImageOverlayMiddleware.ComputeETag(id, "4k", "tag_Primary_default__Jpeg", "cfg", "", false);

        Assert.AreEqual(baseline, ImageOverlayMiddleware.ComputeETag(id, "4k", "tag_Primary_default__Jpeg", "cfg", "", false));
        Assert.AreNotEqual(baseline, ImageOverlayMiddleware.ComputeETag(Guid.NewGuid(), "4k", "tag_Primary_default__Jpeg", "cfg", "", false));
        Assert.AreNotEqual(baseline, ImageOverlayMiddleware.ComputeETag(id, "4k_hdr10", "tag_Primary_default__Jpeg", "cfg", "", false));
        Assert.AreNotEqual(baseline, ImageOverlayMiddleware.ComputeETag(id, "4k", "tag_Primary_samsung-no-dv__Jpeg", "cfg", "", false));
        Assert.AreNotEqual(baseline, ImageOverlayMiddleware.ComputeETag(id, "4k", "tag_Primary_default__WebP", "cfg", "", false));
        Assert.AreNotEqual(baseline, ImageOverlayMiddleware.ComputeETag(id, "4k", "tag_Primary_default__Jpeg", "cfg2", "", false));
        Assert.AreNotEqual(baseline, ImageOverlayMiddleware.ComputeETag(id, "4k", "tag_Primary_default__Jpeg", "cfg", "custom", false));
        Assert.AreNotEqual(baseline, ImageOverlayMiddleware.ComputeETag(id, "4k", "tag_Primary_default__Jpeg", "cfg", "", true));
    }

    [TestMethod]
    public void ComputeCustomBadgeFingerprint_ChangesWhenABadgeIsUploaded()
    {
        var dir = Path.Combine(Path.GetTempPath(), "JellyTagTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.AreEqual(string.Empty, ImageOverlayService.ComputeCustomBadgeFingerprint(Path.Combine(dir, "missing")));
            var empty = ImageOverlayService.ComputeCustomBadgeFingerprint(dir);

            File.WriteAllBytes(Path.Combine(dir, "badge-4k.png"), [1, 2, 3]);

            Assert.AreNotEqual(empty, ImageOverlayService.ComputeCustomBadgeFingerprint(dir));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ----- Output format follows what the client accepts -----

    [TestMethod]
    [DataRow(OutputImageFormat.WebP, null, "", OutputImageFormat.Jpeg)]
    [DataRow(OutputImageFormat.WebP, "*/*", "", OutputImageFormat.Jpeg)]
    [DataRow(OutputImageFormat.WebP, "image/jpeg,image/png", "", OutputImageFormat.Jpeg)]
    [DataRow(OutputImageFormat.WebP, "image/webp;q=0, */*", "", OutputImageFormat.Jpeg)]
    [DataRow(OutputImageFormat.WebP, "image/avif,image/webp,image/apng,image/*,*/*;q=0.8", "", OutputImageFormat.WebP)]
    [DataRow(OutputImageFormat.WebP, null, "?format=webp", OutputImageFormat.WebP)]
    [DataRow(OutputImageFormat.WebP, null, "?format=Webp", OutputImageFormat.WebP)]
    [DataRow(OutputImageFormat.WebP, "image/webp", "?format=jpg", OutputImageFormat.Jpeg)]
    [DataRow(OutputImageFormat.Jpeg, "image/webp", "?format=webp", OutputImageFormat.Jpeg)]
    public void ResolveOutputFormat_FallsBackToJpegForClientsWithoutWebP(OutputImageFormat configured, string? accept, string query, OutputImageFormat expected)
    {
        var context = new DefaultHttpContext();
        if (accept != null) context.Request.Headers.Accept = accept;
        context.Request.QueryString = new QueryString(query);

        Assert.AreEqual(expected, ImageOverlayMiddleware.ResolveOutputFormat(configured, context.Request));
    }

    [TestMethod]
    public async Task InvokeAsync_WebPConfigured_ClientWithoutWebPSupportGetsJpeg()
    {
        using var overlay = new ImageOverlayService(NullLogger<ImageOverlayService>.Instance);
        var env = new Env(overlay);
        env.Config.OutputFormat = OutputImageFormat.WebP;
        using var scope = PluginConfigurationScope.Override(env.Config);

        var legacyClient = await env.SendAsync(ctx => ctx.Request.Headers.Accept = "image/jpeg,image/png,*/*");
        var browser = await env.SendAsync(ctx => ctx.Request.Headers.Accept = "image/avif,image/webp,*/*;q=0.8");
        var explicitFormat = await env.SendAsync(ctx => ctx.Request.QueryString = new QueryString("?format=webp"));

        Assert.AreEqual("image/jpeg", legacyClient.Response.ContentType);
        Assert.AreEqual("image/jpeg", DetectContentType(legacyClient.Body));
        Assert.AreEqual("image/webp", browser.Response.ContentType);
        Assert.AreEqual("image/webp", DetectContentType(browser.Body));
        Assert.AreEqual("image/webp", explicitFormat.Response.ContentType);

        // Both encodings live side by side in the disk cache and have their own ETag.
        Assert.HasCount(3, env.Cache.Entries);
        Assert.AreEqual(3, env.Cache.Entries.Keys.Select(k => k.ImageTag).Distinct().Count());
        Assert.AreNotEqual(legacyClient.Response.Headers.ETag.ToString(), browser.Response.Headers.ETag.ToString());

        // A WebP-capable client revalidating its copy is not handed the JPEG one's ETag, and vice versa.
        var crossed = await env.SendAsync(ctx =>
        {
            ctx.Request.Headers.Accept = "image/jpeg";
            ctx.Request.Headers.IfNoneMatch = browser.Response.Headers.ETag.ToString();
        });
        Assert.AreEqual(200, crossed.Response.StatusCode);
        Assert.AreEqual("image/jpeg", crossed.Response.ContentType);
    }

    [TestMethod]
    public async Task AddBadgeOverlaysAsync_ExplicitFormatOverridesConfiguredOne()
    {
        using var overlay = new ImageOverlayService(NullLogger<ImageOverlayService>.Instance);
        var config = new PluginConfiguration { Enabled = true, OutputFormat = OutputImageFormat.WebP };
        using var scope = PluginConfigurationScope.Override(config);

        var (stream, contentType) = await overlay.AddBadgeOverlaysAsync(
            new MemoryStream(CreateJpeg(300, 450)), [Badge4K], config.PosterConfig, ImageDisplayContext.Card, OutputImageFormat.Jpeg);
        await using (stream)
        {
            Assert.AreEqual("image/jpeg", contentType);
            Assert.AreEqual("image/jpeg", ImageOverlayService.DetectImageContentType(stream));
        }
    }

    // ----- helpers -----

    private static string DetectContentType(byte[] bytes) => ImageOverlayService.DetectImageContentType(new MemoryStream(bytes));

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

    /// <summary>
    /// One movie served through <see cref="ImageOverlayMiddleware"/> with a fake Jellyfin behind it.
    /// </summary>
    private sealed class Env
    {
        private static readonly byte[] Original = CreateJpeg(300, 450);
        private readonly IImageOverlayService _overlay;

        public Env(IImageOverlayService? overlay = null)
        {
            Overlay = new CountingOverlay(overlay ?? new FakeOverlay());
            _overlay = Overlay;
            LibraryManager = LibraryManagerProxy.Create(Movie);
        }

        public PluginConfiguration Config { get; } = new() { Enabled = true };

        public Movie Movie { get; } = new() { Id = Guid.NewGuid(), Name = "Movie", DateModified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };

        public FixedBadges Quality { get; } = new(Badge4K);

        public CountingOverlay Overlay { get; }

        public MemoryCache Cache { get; } = new();

        public ILibraryManager LibraryManager { get; }

        public int NextCalls { get; private set; }

        public Action<HttpContext>? OnNext { get; set; }

        public async Task<(HttpResponse Response, byte[] Body)> SendAsync(Action<HttpContext>? configure = null)
        {
            var middleware = new ImageOverlayMiddleware(
                async ctx =>
                {
                    NextCalls++;
                    OnNext?.Invoke(ctx);
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "image/jpeg";
                    ctx.Response.Headers.ETag = "\"jellyfin-original\"";
                    await ctx.Response.Body.WriteAsync(Original);
                },
                NullLogger<ImageOverlayMiddleware>.Instance);

            var context = new DefaultHttpContext();
            context.Request.Method = "GET";
            context.Request.Path = $"/Items/{Movie.Id:N}/Images/Primary";
            var body = new MemoryStream();
            context.Response.Body = body;
            configure?.Invoke(context);

            await middleware.InvokeAsync(context, Quality, _overlay, Cache, LibraryManager);

            return (context.Response, body.ToArray());
        }
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

    /// <summary>
    /// Appends a byte instead of drawing, so the middleware tests stay independent of rendering.
    /// </summary>
    private sealed class FakeOverlay : IImageOverlayService
    {
        public async Task<(Stream Stream, string ContentType)> AddBadgeOverlaysAsync(
            Stream originalImage, List<BadgeInfo> badges, ImageTypeConfig imageConfig,
            ImageDisplayContext displayContext = ImageDisplayContext.Card, OutputImageFormat? outputFormat = null)
        {
            var output = new MemoryStream();
            await originalImage.CopyToAsync(output);
            output.WriteByte(0xFF);
            output.Position = 0;
            return (output, "image/jpeg");
        }

        public bool ShouldShowBadge(BadgeInfo badge, ImageTypeConfig imageConfig) => true;

        public void ReloadBadges()
        {
        }
    }

    private sealed class CountingOverlay : IImageOverlayService
    {
        private readonly IImageOverlayService _inner;

        public CountingOverlay(IImageOverlayService inner)
        {
            _inner = inner;
        }

        public int Calls { get; private set; }

        public Task<(Stream Stream, string ContentType)> AddBadgeOverlaysAsync(
            Stream originalImage, List<BadgeInfo> badges, ImageTypeConfig imageConfig,
            ImageDisplayContext displayContext = ImageDisplayContext.Card, OutputImageFormat? outputFormat = null)
        {
            Calls++;
            return _inner.AddBadgeOverlaysAsync(originalImage, badges, imageConfig, displayContext, outputFormat);
        }

        public bool ShouldShowBadge(BadgeInfo badge, ImageTypeConfig imageConfig) => _inner.ShouldShowBadge(badge, imageConfig);

        public void ReloadBadges() => _inner.ReloadBadges();
    }

    private sealed class MemoryCache : IImageCacheService
    {
        public Dictionary<(Guid ItemId, string BadgeKey, string ImageTag), byte[]> Entries { get; } = new();

        public Task<Stream?> GetCachedImageAsync(Guid itemId, string badgeKey, string imageTag)
            => Task.FromResult<Stream?>(Entries.TryGetValue((itemId, badgeKey, imageTag), out var bytes) ? new MemoryStream(bytes) : null);

        public async Task CacheImageAsync(Guid itemId, string badgeKey, string imageTag, Stream imageStream)
        {
            using var copy = new MemoryStream();
            await imageStream.CopyToAsync(copy);
            Entries[(itemId, badgeKey, imageTag)] = copy.ToArray();
        }

        public void ClearCache() => Entries.Clear();

        public string GetCacheDirectory() => string.Empty;

        public (int FileCount, long TotalSizeBytes, DateTime? OldestEntry, DateTime? NewestEntry) GetCacheStats() => (Entries.Count, 0, null, null);
    }

    /// <summary>
    /// Minimal <see cref="ILibraryManager"/> that knows a single item.
    /// </summary>
    public class LibraryManagerProxy : DispatchProxy
    {
        private BaseItem? _item;

        public static ILibraryManager Create(BaseItem item)
        {
            var proxy = DispatchProxy.Create<ILibraryManager, LibraryManagerProxy>();
            ((LibraryManagerProxy)(object)proxy)._item = item;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod?.Name switch
            {
                "GetItemById" when args?.Length == 1 && args[0] is Guid id => id == _item?.Id ? _item : null,
                _ => throw new NotSupportedException(targetMethod?.Name)
            };
        }
    }
}
