using System.Reflection;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyTag.Configuration;
using Jellyfin.Plugin.JellyTag.Middleware;
using Jellyfin.Plugin.JellyTag.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jellyfin.Plugin.JellyTag.Tests;

[TestClass]
public class UserFilterTests
{
    private static readonly Guid Me = Guid.NewGuid();
    private static readonly Guid Someone = Guid.NewGuid();

    // ----- Who gets badges -----

    [TestMethod]
    public void IsUserAllowed_FollowsModeAndSelection()
    {
        var selected = new[] { Me.ToString("N") };

        Assert.IsTrue(ImageOverlayMiddleware.IsUserAllowed(UserFilterMode.AllUsers, selected, Someone));
        Assert.IsTrue(ImageOverlayMiddleware.IsUserAllowed(UserFilterMode.AllUsers, selected, null));

        Assert.IsTrue(ImageOverlayMiddleware.IsUserAllowed(UserFilterMode.OnlySelected, selected, Me));
        Assert.IsFalse(ImageOverlayMiddleware.IsUserAllowed(UserFilterMode.OnlySelected, selected, Someone));
        Assert.IsFalse(ImageOverlayMiddleware.IsUserAllowed(UserFilterMode.OnlySelected, selected, null), "An unidentified user is never selected");

        Assert.IsFalse(ImageOverlayMiddleware.IsUserAllowed(UserFilterMode.AllExceptSelected, selected, Me));
        Assert.IsTrue(ImageOverlayMiddleware.IsUserAllowed(UserFilterMode.AllExceptSelected, selected, Someone));
        Assert.IsTrue(ImageOverlayMiddleware.IsUserAllowed(UserFilterMode.AllExceptSelected, selected, null));
    }

    [TestMethod]
    public void IsUserAllowed_AcceptsDashedIds()
    {
        Assert.IsTrue(ImageOverlayMiddleware.IsUserAllowed(UserFilterMode.OnlySelected, [Me.ToString("D").ToUpperInvariant()], Me));
    }

    // ----- Identifying the user of an image request -----

    [TestMethod]
    public async Task ResolveUserId_FromToken_UsesJellyfinAuthorizationAndCachesIt()
    {
        var token = Guid.NewGuid().ToString("N");
        var auth = AuthorizationContextProxy.Create(UserWithId(Me));
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = $"MediaBrowser Client=\"Android TV\", DeviceId=\"tv\", Token=\"{token}\"";

        var first = await ImageOverlayMiddleware.ResolveUserIdAsync(context, ImageOverlayMiddlewareTests.SessionManagerProxy.Create(), auth);
        var second = await ImageOverlayMiddleware.ResolveUserIdAsync(context, ImageOverlayMiddlewareTests.SessionManagerProxy.Create(), auth);

        Assert.AreEqual(Me, first);
        Assert.AreEqual(Me, second);
        Assert.AreEqual(1, ((AuthorizationContextProxy)(object)auth).Calls, "The token should be looked up once, not per poster");
    }

    [TestMethod]
    [DataRow("api_key")]
    [DataRow("ApiKey")]
    public async Task ResolveUserId_TokenInQuery_IsUsed(string parameter)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString($"?{parameter}={Guid.NewGuid():N}");

        var userId = await ImageOverlayMiddleware.ResolveUserIdAsync(context, null, AuthorizationContextProxy.Create(UserWithId(Me)));

        Assert.AreEqual(Me, userId);
    }

    [TestMethod]
    public async Task ResolveUserId_ApiKeyWithoutUser_FallsBackToSession()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Emby-Token"] = Guid.NewGuid().ToString("N");
        context.Request.Headers["X-Emby-Device-Id"] = "living-room";
        var sessions = ImageOverlayMiddlewareTests.SessionManagerProxy.Create(Session("living-room", Me, "10.0.0.5"));

        var userId = await ImageOverlayMiddleware.ResolveUserIdAsync(context, sessions, AuthorizationContextProxy.Create(user: null));

        Assert.AreEqual(Me, userId);
    }

    [TestMethod]
    public async Task ResolveUserId_WithoutToken_UsesTheOnlySessionOnTheIp()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.0.7");
        var sessions = ImageOverlayMiddlewareTests.SessionManagerProxy.Create(
            Session("browser", Me, "10.0.0.7:52311"),
            Session("tv", Someone, "10.0.0.8:41000"));

        Assert.AreEqual(Me, await ImageOverlayMiddleware.ResolveUserIdAsync(context, sessions, null));
    }

    [TestMethod]
    public async Task ResolveUserId_SharedIpWithSeveralSessions_IsUnknown()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.0.7");
        var sessions = ImageOverlayMiddlewareTests.SessionManagerProxy.Create(
            Session("browser", Me, "10.0.0.7:52311"),
            Session("phone", Someone, "10.0.0.7:52400"));

        Assert.IsNull(await ImageOverlayMiddleware.ResolveUserIdAsync(context, sessions, null));
    }

    // ----- End to end through the middleware -----

    [TestMethod]
    public async Task OnlySelected_OtherUser_GetsTheOriginalImage()
    {
        var env = new Env(UserFilterMode.OnlySelected, Me);

        await env.SendAsync(Session("tv", Someone, "10.0.0.8"));

        Assert.AreEqual(1, env.NextCalls);
        Assert.AreEqual(0, env.Overlay.Calls, "No badges for users who aren't selected");
    }

    [TestMethod]
    public async Task OnlySelected_SelectedUser_GetsBadges()
    {
        var env = new Env(UserFilterMode.OnlySelected, Me);

        await env.SendAsync(Session("tv", Me, "10.0.0.8"));

        Assert.AreEqual(1, env.Overlay.Calls);
    }

    [TestMethod]
    public async Task AllExceptSelected_ExcludedUser_GetsTheOriginalImage()
    {
        var env = new Env(UserFilterMode.AllExceptSelected, Someone);

        await env.SendAsync(Session("tv", Someone, "10.0.0.8"));

        Assert.AreEqual(0, env.Overlay.Calls);
    }

    private static SessionInfo Session(string deviceId, Guid userId, string remoteEndPoint)
    {
        return new SessionInfo(null!, null!) { DeviceId = deviceId, UserId = userId, RemoteEndPoint = remoteEndPoint };
    }

    private static User UserWithId(Guid id)
    {
        var user = new User("tester", "Default", "Default");
        typeof(User).GetProperty(nameof(User.Id))!.SetValue(user, id);
        return user;
    }

    /// <summary>
    /// <see cref="IAuthorizationContext"/> that returns a fixed user and counts lookups.
    /// </summary>
    public class AuthorizationContextProxy : DispatchProxy
    {
        private User? _user;

        public int Calls { get; private set; }

        public static IAuthorizationContext Create(User? user)
        {
            var proxy = DispatchProxy.Create<IAuthorizationContext, AuthorizationContextProxy>();
            ((AuthorizationContextProxy)(object)proxy)._user = user;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IAuthorizationContext.GetAuthorizationInfo))
            {
                throw new NotSupportedException(targetMethod?.Name);
            }

            Calls++;
            return Task.FromResult(new AuthorizationInfo { User = _user!, IsAuthenticated = true, IsApiKey = _user == null });
        }
    }

    /// <summary>
    /// One movie behind the middleware, requested from a device with a known session.
    /// </summary>
    private sealed class Env
    {
        private readonly Movie _movie = new() { Id = Guid.NewGuid(), Name = "Movie" };
        private readonly PluginConfiguration _config;

        public Env(UserFilterMode mode, Guid selectedUser)
        {
            _config = new PluginConfiguration { Enabled = true, UserFilterMode = mode, FilteredUserIds = [selectedUser.ToString("N")] };
        }

        public CountingOverlay Overlay { get; } = new();

        public int NextCalls { get; private set; }

        public async Task SendAsync(SessionInfo session)
        {
            using var scope = PluginConfigurationScope.Override(_config);
            var middleware = new ImageOverlayMiddleware(
                async ctx =>
                {
                    NextCalls++;
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "image/jpeg";
                    await ctx.Response.Body.WriteAsync(new byte[] { 0xFF, 0xD8, 0xFF });
                },
                NullLogger<ImageOverlayMiddleware>.Instance);

            var context = new DefaultHttpContext();
            context.Request.Method = "GET";
            context.Request.Path = $"/Items/{_movie.Id:N}/Images/Primary";
            context.Request.Headers["X-Emby-Device-Id"] = session.DeviceId;
            context.Response.Body = new MemoryStream();

            await middleware.InvokeAsync(
                context,
                new FixedBadges(),
                Overlay,
                new NoCache(),
                ImageHttpCachingTests.LibraryManagerProxy.Create(_movie),
                ImageOverlayMiddlewareTests.SessionManagerProxy.Create(session));
        }
    }

    private sealed class FixedBadges : IQualityDetectionService
    {
        public List<BadgeInfo> DetectAllBadges(BaseItem item) =>
            [new BadgeInfo { Category = BadgeCategory.Resolution, BadgeKey = "4k", ResourceFileName = "badge-4k.svg" }];

        public void ClearBadgeCache()
        {
        }
    }

    private sealed class CountingOverlay : IImageOverlayService
    {
        public int Calls { get; private set; }

        public Task<(Stream Stream, string ContentType)> AddBadgeOverlaysAsync(
            Stream originalImage,
            List<BadgeInfo> badges,
            ImageTypeConfig imageConfig,
            ImageDisplayContext displayContext = ImageDisplayContext.Card,
            OutputImageFormat? outputFormat = null)
        {
            Calls++;
            return Task.FromResult<(Stream, string)>((new MemoryStream(new byte[] { 1 }), "image/jpeg"));
        }

        public bool ShouldShowBadge(BadgeInfo badge, ImageTypeConfig imageConfig) => true;

        public void ReloadBadges()
        {
        }
    }

    private sealed class NoCache : IImageCacheService
    {
        public Task<Stream?> GetCachedImageAsync(Guid itemId, string badgeKey, string imageTag) => Task.FromResult<Stream?>(null);

        public Task CacheImageAsync(Guid itemId, string badgeKey, string imageTag, Stream imageStream) => Task.CompletedTask;

        public void ClearCache()
        {
        }

        public string GetCacheDirectory() => string.Empty;

        public (int FileCount, long TotalSizeBytes, DateTime? OldestEntry, DateTime? NewestEntry) GetCacheStats() => (0, 0, null, null);
    }
}
