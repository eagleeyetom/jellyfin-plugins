using System.Net;
using System.Reflection;
using Jellyfin.Plugin.JellyTag.Configuration;
using Jellyfin.Plugin.JellyTag.Middleware;
using Jellyfin.Plugin.JellyTag.Services;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jellyfin.Plugin.JellyTag.Tests;

[TestClass]
public class ImageOverlayMiddlewareTests
{
    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];

    /// <summary>
    /// Minimal <see cref="ISessionManager"/> that only answers <c>Sessions</c>.
    /// </summary>
    public class SessionManagerProxy : DispatchProxy
    {
        public IReadOnlyList<SessionInfo> Sessions { get; set; } = [];

        public static ISessionManager Create(params SessionInfo[] sessions)
        {
            var proxy = DispatchProxy.Create<ISessionManager, SessionManagerProxy>();
            ((SessionManagerProxy)(object)proxy).Sessions = sessions;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod?.Name == "get_Sessions"
                ? Sessions
                : throw new NotSupportedException(targetMethod?.Name);
        }
    }

    /// <summary>
    /// <see cref="ISessionController"/> whose <c>IsSessionActive</c> is configurable.
    /// </summary>
    public class SessionControllerProxy : DispatchProxy
    {
        public bool IsActive { get; set; }

        public static ISessionController Create(bool isActive)
        {
            var proxy = DispatchProxy.Create<ISessionController, SessionControllerProxy>();
            ((SessionControllerProxy)(object)proxy).IsActive = isActive;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod?.Name switch
            {
                "get_IsSessionActive" => IsActive,
                "get_SupportsMediaControl" => false,
                _ => throw new NotSupportedException(targetMethod?.Name)
            };
        }
    }

    private sealed class FakeCacheService : IImageCacheService
    {
        public byte[]? CachedBytes { get; set; }

        public int CacheWrites { get; private set; }

        public Task<Stream?> GetCachedImageAsync(Guid itemId, string badgeKey, string imageTag)
            => Task.FromResult<Stream?>(CachedBytes == null ? null : new MemoryStream(CachedBytes));

        public Task CacheImageAsync(Guid itemId, string badgeKey, string imageTag, Stream imageStream)
        {
            CacheWrites++;
            return Task.CompletedTask;
        }

        public void ClearCache() { }

        public string GetCacheDirectory() => string.Empty;

        public (int FileCount, long TotalSizeBytes, DateTime? OldestEntry, DateTime? NewestEntry) GetCacheStats() => (0, 0, null, null);
    }

    private sealed class FakeOverlayService : IImageOverlayService
    {
        public int Calls { get; private set; }

        public async Task<(Stream Stream, string ContentType)> AddBadgeOverlaysAsync(
            Stream originalImage,
            List<BadgeInfo> badges,
            ImageTypeConfig imageConfig,
            ImageDisplayContext displayContext = ImageDisplayContext.Card,
            OutputImageFormat? outputFormat = null)
        {
            Calls++;
            var output = new MemoryStream();
            await originalImage.CopyToAsync(output);
            output.WriteByte(0xFF);
            output.Position = 0;
            return (output, "image/png");
        }

        public bool ShouldShowBadge(BadgeInfo badge, ImageTypeConfig imageConfig) => true;

        public void ReloadBadges() { }
    }

    private static SessionInfo CreateSession(string client, string deviceName, string remoteEndPoint, bool? isActive = null)
    {
        var session = new SessionInfo(null!, null!)
        {
            Client = client,
            DeviceName = deviceName,
            DeviceId = Guid.NewGuid().ToString("N"),
            RemoteEndPoint = remoteEndPoint
        };

        if (isActive.HasValue)
        {
            session.SessionControllers = [SessionControllerProxy.Create(isActive.Value)];
        }

        return session;
    }

    private static DefaultHttpContext CreateBrowserRequest(string ip)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        context.Request.Headers.UserAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 Chrome/120.0";
        return context;
    }

    [TestMethod]
    public void GetClientIdentifiers_SingleSessionOnIp_UsesThatSession()
    {
        var sessionManager = SessionManagerProxy.Create(
            CreateSession("Samsung Smart TV", "Living Room", "192.168.1.10:51234"));

        var identifiers = ImageOverlayMiddleware.GetClientIdentifiers(CreateBrowserRequest("192.168.1.10"), sessionManager);

        Assert.IsTrue(identifiers.Any(ImageOverlayMiddleware.ContainsSamsungIndicator));
    }

    [TestMethod]
    public void GetClientIdentifiers_SeveralSessionsOnSameIp_UsesNone()
    {
        // Household behind one NAT (or a proxy without X-Forwarded-For): the Samsung TV
        // session must not leak its "no DV" filtering onto the web browser on the same IP.
        var sessionManager = SessionManagerProxy.Create(
            CreateSession("Samsung Smart TV", "Living Room", "192.168.1.10:51234"),
            CreateSession("Jellyfin Web", "Chrome", "192.168.1.10:51300"));

        var context = CreateBrowserRequest("192.168.1.10");

        Assert.IsFalse(ImageOverlayMiddleware.IsSamsungClient(context, sessionManager));
        Assert.IsFalse(ImageOverlayMiddleware.IsFireTvClient(context, sessionManager));
        Assert.IsFalse(ImageOverlayMiddleware.IsWindowsClient(context, sessionManager));
    }

    [TestMethod]
    public void GetClientIdentifiers_SeveralSessionsOnIpButOneActive_UsesActiveSession()
    {
        var sessionManager = SessionManagerProxy.Create(
            CreateSession("Jellyfin Web", "Chrome", "192.168.1.10:51300", isActive: false),
            CreateSession("Jellyfin Android TV", "Fire TV Stick 4K", "192.168.1.10:51234", isActive: true));

        Assert.IsTrue(ImageOverlayMiddleware.IsFireTvClient(CreateBrowserRequest("192.168.1.10"), sessionManager));
    }

    [TestMethod]
    public void GetClientIdentifiers_DeviceIdMatch_WinsOverAmbiguousIp()
    {
        var samsung = CreateSession("Samsung Smart TV", "Living Room", "192.168.1.10:51234");
        var sessionManager = SessionManagerProxy.Create(
            samsung,
            CreateSession("Jellyfin Web", "Chrome", "192.168.1.10:51300"));

        var context = CreateBrowserRequest("192.168.1.10");
        context.Request.Headers["X-Emby-Device-Id"] = samsung.DeviceId;

        Assert.IsTrue(ImageOverlayMiddleware.IsSamsungClient(context, sessionManager));
    }

    [TestMethod]
    public void GetClientIdentifiers_TokenEqualToSessionId_DoesNotMatchSession()
    {
        // SessionInfo.Id is not the access token, so a token must never select a session.
        var samsung = CreateSession("Samsung Smart TV", "Living Room", "10.0.0.5:51234");
        samsung.Id = "abc123";
        var sessionManager = SessionManagerProxy.Create(samsung);

        var context = CreateBrowserRequest("192.168.1.10");
        context.Request.Headers["X-Emby-Token"] = "abc123";

        Assert.IsFalse(ImageOverlayMiddleware.IsSamsungClient(context, sessionManager));
    }

    [TestMethod]
    public async Task ServeBadgedImageAsync_CacheHit_DoesNotCallNext()
    {
        var nextCalls = 0;
        var middleware = new ImageOverlayMiddleware(
            _ =>
            {
                nextCalls++;
                return Task.CompletedTask;
            },
            NullLogger<ImageOverlayMiddleware>.Instance);
        var cache = new FakeCacheService { CachedBytes = PngHeader };
        var overlay = new FakeOverlayService();
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;

        await middleware.ServeBadgedImageAsync(context, overlay, cache, Guid.NewGuid(), "4k", "tag", [], new ImageTypeConfig(), "\"etag\"", OutputImageFormat.Jpeg);

        Assert.AreEqual(0, nextCalls, "Jellyfin should not read the original image on a cache hit");
        Assert.AreEqual(0, overlay.Calls);
        Assert.AreEqual(200, context.Response.StatusCode);
        Assert.AreEqual("image/png", context.Response.ContentType);
        CollectionAssert.AreEqual(PngHeader, body.ToArray());
    }

    [TestMethod]
    public async Task ServeBadgedImageAsync_CacheMiss_OverlaysAndCachesOriginal()
    {
        var middleware = new ImageOverlayMiddleware(
            ctx =>
            {
                ctx.Response.StatusCode = 200;
                return ctx.Response.Body.WriteAsync(PngHeader).AsTask();
            },
            NullLogger<ImageOverlayMiddleware>.Instance);
        var cache = new FakeCacheService();
        var overlay = new FakeOverlayService();
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;

        await middleware.ServeBadgedImageAsync(context, overlay, cache, Guid.NewGuid(), "4k", "tag", [], new ImageTypeConfig(), "\"etag\"", OutputImageFormat.Jpeg);

        Assert.AreEqual(1, overlay.Calls);
        Assert.AreEqual(1, cache.CacheWrites);
        Assert.AreEqual(PngHeader.Length + 1, body.Length);
        Assert.AreSame(body, context.Response.Body);
    }

    [TestMethod]
    public async Task ServeBadgedImageAsync_CacheMissWithNon200_PassesResponseThrough()
    {
        var notFound = "not found"u8.ToArray();
        var middleware = new ImageOverlayMiddleware(
            ctx =>
            {
                ctx.Response.StatusCode = 404;
                return ctx.Response.Body.WriteAsync(notFound).AsTask();
            },
            NullLogger<ImageOverlayMiddleware>.Instance);
        var cache = new FakeCacheService();
        var overlay = new FakeOverlayService();
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;

        await middleware.ServeBadgedImageAsync(context, overlay, cache, Guid.NewGuid(), "4k", "tag", [], new ImageTypeConfig(), "\"etag\"", OutputImageFormat.Jpeg);

        Assert.AreEqual(404, context.Response.StatusCode);
        Assert.AreEqual(0, overlay.Calls);
        Assert.AreEqual(0, cache.CacheWrites);
        CollectionAssert.AreEqual(notFound, body.ToArray());
    }
}
