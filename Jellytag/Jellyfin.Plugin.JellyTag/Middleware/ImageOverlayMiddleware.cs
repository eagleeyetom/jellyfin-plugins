using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyTag.Configuration;
using Jellyfin.Plugin.JellyTag.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTag.Middleware;

/// <summary>
/// Middleware that intercepts Jellyfin image requests and adds quality badge overlays.
/// </summary>
public partial class ImageOverlayMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ImageOverlayMiddleware> _logger;

    [GeneratedRegex(@"/Items/([0-9a-f]{32}|[0-9a-f-]{36})/Images/(Primary|Thumb)(/\d+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ImagePathRegex();

    [GeneratedRegex(@"\bAFT[A-Z0-9]+\b", RegexOptions.IgnoreCase)]
    private static partial Regex AftModelRegex();

    [GeneratedRegex(@"\bFire\b", RegexOptions.IgnoreCase)]
    private static partial Regex FireWordRegex();

    public ImageOverlayMiddleware(RequestDelegate next, ILogger<ImageOverlayMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext context,
        IQualityDetectionService qualityService,
        IImageOverlayService overlayService,
        IImageCacheService cacheService,
        MediaBrowser.Controller.Library.ILibraryManager libraryManager,
        ISessionManager? sessionManager = null)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var path = context.Request.Path.Value;
        if (path == null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var match = ImagePathRegex().Match(path);
        if (!match.Success)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var config = Plugin.Instance?.Configuration;
        if (config == null || !config.Enabled)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var itemIdStr = match.Groups[1].Value;
        var imageType = match.Groups[2].Value;

        if (!Guid.TryParse(itemIdStr, out var itemId))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var item = libraryManager.GetItemById(itemId);
        if (item == null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (item is not (Movie or Series or Season or Episode or Video))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Check if item's library is excluded
        if (config.ExcludedLibraryIds.Count > 0)
        {
            var excludedGuids = new HashSet<Guid>();
            foreach (var ex in config.ExcludedLibraryIds)
            {
                if (Guid.TryParse(ex, out var g))
                {
                    excludedGuids.Add(g);
                }
            }

            var collectionFolders = libraryManager.GetCollectionFolders(item);
            if (collectionFolders.Any(f => excludedGuids.Contains(f.Id)))
            {
                await _next(context).ConfigureAwait(false);
                return;
            }
        }

        var imageConfig = GetImageTypeConfig(config, imageType, item);
        if (imageConfig == null || !imageConfig.Enabled)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Detect all badges and filter by config
        var allBadges = qualityService.DetectAllBadges(item);
        _logger.LogDebug("DetectAllBadges for {Item}: {Count} badges found: {Badges}",
            item.Name, allBadges.Count, string.Join(", ", allBadges.Select(b => $"{b.Category}:{b.BadgeKey}")));

        sessionManager ??= context.RequestServices?.GetService(typeof(ISessionManager)) as ISessionManager;

        var isSamsung = IsSamsungClient(context, sessionManager);
        var isFireTv = IsFireTvClient(context, sessionManager);
        var isWindows = IsWindowsClient(context, sessionManager);

        _logger.LogDebug("Client identification for {Item} - isFireTv: {IsFireTv}, isSamsung: {IsSamsung}, isWindows: {IsWindows}",
            item.Name, isFireTv, isSamsung, isWindows);

        var hideDolbyVision = (config.HideDolbyVisionOnSamsungClients && isSamsung)
            || (config.HideDolbyVisionOnFireTvClients && isFireTv);
        var hideHdrOnWindows = config.HideHdrOnWindowsClients && isWindows;
        var visibleBadges = allBadges
            .Where(b => overlayService.ShouldShowBadge(b, imageConfig))
            .Where(b => !hideDolbyVision || !string.Equals(b.BadgeKey, "dv", StringComparison.OrdinalIgnoreCase))
            .Where(b => !hideHdrOnWindows || !IsHdrBadge(b.BadgeKey))
            .ToList();
        
        _logger.LogDebug("Visible badges after filter: {Count}: {Badges}",
            visibleBadges.Count, string.Join(", ", visibleBadges.Select(b => b.BadgeKey)));

        if (visibleBadges.Count == 0)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var badgeKey = string.Join("_", visibleBadges.Select(b => b.BadgeKey));
        _logger.LogInformation("Applying {Count} badges to {Item}: {BadgeKey}", visibleBadges.Count, item.Name, badgeKey);

        var query = GetCacheRelevantQuery(context.Request.Query);
        var tag = context.Request.Query["tag"].FirstOrDefault() ?? item.DateModified.Ticks.ToString();
        var dvVariant = hideDolbyVision ? (isSamsung ? "samsung-no-dv" : "firetv-no-dv") : "";
        var clientVariant = dvVariant + (hideHdrOnWindows ? "_windows-sdr" : "");
        if (string.IsNullOrEmpty(clientVariant)) clientVariant = "default";
        var imageTag = $"{tag}_{imageType}_{clientVariant}_{query}";

        context.Request.Headers.Remove("If-None-Match");
        context.Request.Headers.Remove("If-Modified-Since");

        var originalBody = context.Response.Body;
        using var capturedBody = new MemoryStream();
        context.Response.Body = capturedBody;

        try
        {
            await _next(context).ConfigureAwait(false);

            if (context.Response.StatusCode != 200 || capturedBody.Length == 0)
            {
                capturedBody.Position = 0;
                await capturedBody.CopyToAsync(originalBody).ConfigureAwait(false);
                return;
            }

            capturedBody.Position = 0;

            var cachedImage = await cacheService.GetCachedImageAsync(itemId, badgeKey, imageTag).ConfigureAwait(false);
            if (cachedImage != null)
            {
                await using (cachedImage.ConfigureAwait(false))
                {
                    cachedImage.Position = 0;
                    var cachedContentType = ImageOverlayService.DetectImageContentType(cachedImage);
                    ClearEntityHeaders(context.Response);
                    context.Response.ContentType = cachedContentType;
                    context.Response.ContentLength = cachedImage.Length;
                    await cachedImage.CopyToAsync(originalBody).ConfigureAwait(false);
                }

                return;
            }

            (Stream resultStream, string contentType) result;
            try
            {
                result = await overlayService.AddBadgeOverlaysAsync(capturedBody, visibleBadges, imageConfig).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to add badge overlay, serving original image");
                capturedBody.Position = 0;
                await capturedBody.CopyToAsync(originalBody).ConfigureAwait(false);
                return;
            }

            await using (result.resultStream.ConfigureAwait(false))
            {
                result.resultStream.Position = 0;
                await cacheService.CacheImageAsync(itemId, badgeKey, imageTag, result.resultStream).ConfigureAwait(false);

                result.resultStream.Position = 0;
                ClearEntityHeaders(context.Response);
                context.Response.ContentType = result.contentType;
                context.Response.ContentLength = result.resultStream.Length;
                await result.resultStream.CopyToAsync(originalBody).ConfigureAwait(false);
            }
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    internal static string GetCacheRelevantQuery(IQueryCollection query)
    {
        var parameters = query
            .Where(parameter => !parameter.Key.Equals("api_key", StringComparison.OrdinalIgnoreCase))
            .OrderBy(parameter => parameter.Key, StringComparer.OrdinalIgnoreCase)
            .SelectMany(parameter => parameter.Value.Select(value =>
                new KeyValuePair<string, string?>(parameter.Key, value)));

        return QueryString.Create(parameters).Value ?? string.Empty;
    }

    private static void ClearEntityHeaders(HttpResponse response)
    {
        response.Headers.Remove("ETag");
        response.Headers.Remove("Last-Modified");
        response.Headers.Remove("Content-MD5");
        response.Headers.Remove("Accept-Ranges");
        response.Headers.CacheControl = "no-cache";
    }

    private static ImageTypeConfig? GetImageTypeConfig(PluginConfiguration config, string imageType, BaseItem item)
    {
        var type = imageType.ToUpperInvariant();

        var isThumb = type switch
        {
            "PRIMARY" when item is Episode => true,
            "THUMB" => true,
            _ => false
        };

        if (isThumb && config.ThumbnailSameAsPoster)
        {
            return ApplySizeReduction(config.PosterConfig, config.ThumbnailSizeReduction);
        }

        return type switch
        {
            "PRIMARY" when item is Episode => config.ThumbnailConfig,
            "PRIMARY" => config.PosterConfig,
            "THUMB" => config.ThumbnailConfig,
            _ => null
        };
    }

    internal static bool IsSamsungClient(HttpContext context, ISessionManager? sessionManager = null)
    {
        return GetClientIdentifiers(context, sessionManager).Any(ContainsSamsungIndicator);
    }

    internal static bool ContainsSamsungIndicator(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.Contains("Tizen", StringComparison.OrdinalIgnoreCase)) return true;
        if (value.Contains("Samsung", StringComparison.OrdinalIgnoreCase))
        {
            if (value.Contains("Galaxy", StringComparison.OrdinalIgnoreCase)
                || value.Contains("SM-", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return true;
        }
        return false;
    }

    internal static bool IsFireTvClient(HttpContext context, ISessionManager? sessionManager = null)
    {
        return GetClientIdentifiers(context, sessionManager).Any(ContainsFireTvIndicator);
    }

    internal static bool ContainsFireTvIndicator(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        if (value.Contains("Fire TV", StringComparison.OrdinalIgnoreCase)
            || value.Contains("FireTV", StringComparison.OrdinalIgnoreCase)
            || value.Contains("fire-tv", StringComparison.OrdinalIgnoreCase)
            || value.Contains("fire_tv", StringComparison.OrdinalIgnoreCase)
            || value.Contains("FireStick", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Fire Stick", StringComparison.OrdinalIgnoreCase)
            || value.Contains("FireOS", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Amazon", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Silk/", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Silk ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (AftModelRegex().IsMatch(value))
        {
            return true;
        }

        if (FireWordRegex().IsMatch(value) && !value.Contains("Firefox", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    internal static bool IsWindowsClient(HttpContext context, ISessionManager? sessionManager = null)
    {
        return GetClientIdentifiers(context, sessionManager).Any(ContainsWindowsIndicator);
    }

    internal static bool ContainsWindowsIndicator(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return value.Contains("Windows", StringComparison.OrdinalIgnoreCase);
    }

    internal static IEnumerable<string> GetClientIdentifiers(HttpContext context, ISessionManager? sessionManager = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddIfValid(string? str)
        {
            if (!string.IsNullOrWhiteSpace(str))
            {
                seen.Add(str.Trim());
            }
        }

        // 1. Direct request headers
        AddIfValid(context.Request.Headers["X-Emby-Client"].ToString());
        AddIfValid(context.Request.Headers["X-Emby-Device-Name"].ToString());
        AddIfValid(context.Request.Headers["X-Emby-Device"].ToString());
        AddIfValid(context.Request.Headers["X-Emby-Device-Id"].ToString());
        AddIfValid(context.Request.Headers.UserAgent.ToString());

        // 2. Authorization and X-Emby-Authorization headers
        var authHeader = context.Request.Headers.Authorization.ToString();
        var embyAuthHeader = context.Request.Headers["X-Emby-Authorization"].ToString();
        var authDict = ParseAuthorizationHeader(authHeader);
        foreach (var kvp in ParseAuthorizationHeader(embyAuthHeader))
        {
            authDict.TryAdd(kvp.Key, kvp.Value);
        }

        if (authDict.TryGetValue("Client", out var authClient)) AddIfValid(authClient);
        if (authDict.TryGetValue("Device", out var authDevice)) AddIfValid(authDevice);
        if (authDict.TryGetValue("DeviceName", out var authDeviceName)) AddIfValid(authDeviceName);
        if (authDict.TryGetValue("DeviceId", out var authDeviceId)) AddIfValid(authDeviceId);

        // 3. Query string parameters
        AddIfValid(context.Request.Query["client"].ToString());
        AddIfValid(context.Request.Query["Client"].ToString());
        AddIfValid(context.Request.Query["device"].ToString());
        AddIfValid(context.Request.Query["Device"].ToString());
        AddIfValid(context.Request.Query["deviceName"].ToString());
        AddIfValid(context.Request.Query["DeviceName"].ToString());
        AddIfValid(context.Request.Query["deviceId"].ToString());
        AddIfValid(context.Request.Query["DeviceId"].ToString());

        // 4. SessionManager lookup
        sessionManager ??= context.RequestServices?.GetService(typeof(ISessionManager)) as ISessionManager;
        if (sessionManager != null)
        {
            var token = authDict.GetValueOrDefault("Token");
            if (string.IsNullOrEmpty(token)) token = context.Request.Headers["X-Emby-Token"].ToString();
            if (string.IsNullOrEmpty(token)) token = context.Request.Headers["X-MediaBrowser-Token"].ToString();
            if (string.IsNullOrEmpty(token)) token = context.Request.Query["api_key"].ToString();
            if (string.IsNullOrEmpty(token)) token = context.Request.Query["token"].ToString();
            if (string.IsNullOrEmpty(token)) token = context.Request.Query["X-Emby-Token"].ToString();

            var deviceId = authDict.GetValueOrDefault("DeviceId");
            if (string.IsNullOrEmpty(deviceId)) deviceId = context.Request.Headers["X-Emby-Device-Id"].ToString();
            if (string.IsNullOrEmpty(deviceId)) deviceId = context.Request.Query["deviceId"].ToString();
            if (string.IsNullOrEmpty(deviceId)) deviceId = context.Request.Query["DeviceId"].ToString();

            var clientIp = GetClientIp(context);

            try
            {
                var sessions = sessionManager.Sessions;
                if (sessions != null)
                {
                    SessionInfo? matchedSession = null;
                    if (!string.IsNullOrEmpty(deviceId))
                    {
                        matchedSession = sessions.FirstOrDefault(s => string.Equals(s.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
                    }

                    if (matchedSession == null && !string.IsNullOrEmpty(token))
                    {
                        matchedSession = sessions.FirstOrDefault(s => string.Equals(s.Id, token, StringComparison.OrdinalIgnoreCase));
                    }

                    if (matchedSession != null)
                    {
                        AddSessionIdentifiers(matchedSession, AddIfValid);
                    }
                    else if (!string.IsNullOrEmpty(clientIp))
                    {
                        var ipSessions = sessions
                            .Where(s => IsIpMatch(s.RemoteEndPoint, clientIp))
                            .OrderByDescending(s => s.IsActive)
                            .ThenByDescending(s => s.LastActivityDate)
                            .ToList();

                        foreach (var session in ipSessions)
                        {
                            AddSessionIdentifiers(session, AddIfValid);
                        }
                    }
                }
            }
            catch
            {
                // Ignore any session query exceptions to avoid breaking image serving
            }
        }

        return seen;
    }

    private static void AddSessionIdentifiers(SessionInfo session, Action<string?> addIfValid)
    {
        addIfValid(session.DeviceName);
        addIfValid(session.Client);
        addIfValid(session.DeviceType);
        addIfValid(session.ApplicationVersion);
        if (session.Capabilities?.DeviceProfile != null)
        {
            addIfValid(session.Capabilities.DeviceProfile.Name);
        }
    }

    internal static Dictionary<string, string> ParseAuthorizationHeader(string? authHeader)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(authHeader))
        {
            return dict;
        }

        var header = authHeader.Trim();
        if (header.StartsWith("MediaBrowser ", StringComparison.OrdinalIgnoreCase))
        {
            header = header["MediaBrowser ".Length..].Trim();
        }
        else if (header.StartsWith("Custom ", StringComparison.OrdinalIgnoreCase))
        {
            header = header["Custom ".Length..].Trim();
        }

        var parts = header.Split(',');
        foreach (var part in parts)
        {
            var eqIdx = part.IndexOf('=');
            if (eqIdx <= 0) continue;

            var key = part[..eqIdx].Trim();
            var val = part[(eqIdx + 1)..].Trim().Trim('"', '\'');
            if (!string.IsNullOrEmpty(key))
            {
                dict[key] = val;
            }
        }

        return dict;
    }

    internal static string? GetClientIp(HttpContext context)
    {
        var forwardedFor = context.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrWhiteSpace(forwardedFor))
        {
            var firstIp = forwardedFor.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrEmpty(firstIp))
            {
                return firstIp;
            }
        }

        var realIp = context.Request.Headers["X-Real-IP"].ToString();
        if (!string.IsNullOrWhiteSpace(realIp))
        {
            return realIp.Trim();
        }

        var remoteIp = context.Connection.RemoteIpAddress;
        if (remoteIp != null)
        {
            if (remoteIp.IsIPv4MappedToIPv6)
            {
                return remoteIp.MapToIPv4().ToString();
            }

            return remoteIp.ToString();
        }

        return null;
    }

    internal static bool IsIpMatch(string? sessionRemoteEndPoint, string? requestIp)
    {
        if (string.IsNullOrWhiteSpace(sessionRemoteEndPoint) || string.IsNullOrWhiteSpace(requestIp))
        {
            return false;
        }

        sessionRemoteEndPoint = sessionRemoteEndPoint.Trim();
        requestIp = requestIp.Trim();

        if (string.Equals(sessionRemoteEndPoint, requestIp, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var colonIdx = sessionRemoteEndPoint.LastIndexOf(':');
        if (colonIdx > 0 && !sessionRemoteEndPoint.Contains(']'))
        {
            var epIp = sessionRemoteEndPoint[..colonIdx];
            if (string.Equals(epIp, requestIp, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        else if (sessionRemoteEndPoint.StartsWith('[') && sessionRemoteEndPoint.Contains("]:"))
        {
            var closeBracket = sessionRemoteEndPoint.IndexOf(']');
            var epIp = sessionRemoteEndPoint.Substring(1, closeBracket - 1);
            if (string.Equals(epIp, requestIp, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsHdrBadge(string badgeKey)
    {
        return string.Equals(badgeKey, "dv", StringComparison.OrdinalIgnoreCase)
            || string.Equals(badgeKey, "hdr10", StringComparison.OrdinalIgnoreCase)
            || string.Equals(badgeKey, "hdr10plus", StringComparison.OrdinalIgnoreCase)
            || string.Equals(badgeKey, "hlg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(badgeKey, "hdr", StringComparison.OrdinalIgnoreCase);
    }

    internal static ImageTypeConfig ApplySizeReduction(ImageTypeConfig source, int reduction)
    {
        if (reduction <= 0) return source;

        var clone = new ImageTypeConfig
        {
            Enabled = source.Enabled,
            ResolutionPanel = ClonePanelWithReduction(source.ResolutionPanel, reduction),
            HdrPanel = ClonePanelWithReduction(source.HdrPanel, reduction),
            CodecPanel = ClonePanelWithReduction(source.CodecPanel, reduction),
            AudioPanel = ClonePanelWithReduction(source.AudioPanel, reduction),
            ChannelPanel = ClonePanelWithReduction(source.ChannelPanel, reduction),
            LanguagePanel = ClonePanelWithReduction(source.LanguagePanel, reduction),
            ShowVostIndicator = source.ShowVostIndicator,
            VostBgColor = source.VostBgColor,
            VostTextColor = source.VostTextColor,
            VostBgOpacity = source.VostBgOpacity,
            VostCornerRadius = source.VostCornerRadius
        };
        return clone;
    }

    internal static BadgePanelSettings ClonePanelWithReduction(BadgePanelSettings panel, int reduction)
    {
        return new BadgePanelSettings
        {
            Enabled = panel.Enabled,
            Position = panel.Position,
            ShowMode = panel.ShowMode,
            Layout = panel.Layout,
            GapPercent = panel.GapPercent,
            SizePercent = Math.Max(1, panel.SizePercent - reduction),
            MarginPercent = panel.MarginPercent,
            Style = panel.Style,
            IconStyle = panel.IconStyle,
            Order = panel.Order,
            TextBgColor = panel.TextBgColor,
            TextBgOpacity = panel.TextBgOpacity,
            TextColor = panel.TextColor,
            TextCornerRadius = panel.TextCornerRadius,
            BadgeTypeOverrides = new List<BadgeTypeStyleOverride>(panel.BadgeTypeOverrides),
            EnabledBadges = new List<string>(panel.EnabledBadges),
            DisabledLogos = new List<string>(panel.DisabledLogos),
            WhiteLogoBackground = panel.WhiteLogoBackground
        };
    }
}
