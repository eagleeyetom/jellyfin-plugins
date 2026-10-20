using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Plugin.MetadataNotifier.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetadataNotifier.Middleware;

/// <summary>
/// Reads the device profile clients post with <c>POST /Items/{id}/PlaybackInfo</c> and stores its
/// video range rules per device, so the playback toast can report what the client can actually
/// display instead of guessing from brand names. The request itself is passed on unchanged.
/// </summary>
public partial class PlaybackInfoCaptureMiddleware
{
    private const long MaxBodyBytes = 2 * 1024 * 1024;

    private readonly RequestDelegate _next;
    private readonly ILogger<PlaybackInfoCaptureMiddleware> _logger;

    // Not anchored at the start so it also matches when Jellyfin runs under a BaseUrl
    // (this middleware runs before UsePathBase).
    [GeneratedRegex(@"/Items/[0-9a-fA-F-]{32,36}/PlaybackInfo/?$", RegexOptions.IgnoreCase)]
    private static partial Regex PlaybackInfoPathRegex();

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackInfoCaptureMiddleware"/> class.
    /// </summary>
    public PlaybackInfoCaptureMiddleware(RequestDelegate next, ILogger<PlaybackInfoCaptureMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Captures the device profile from PlaybackInfo requests, then continues the pipeline.
    /// </summary>
    public async Task InvokeAsync(HttpContext context, ClientCapabilityStore store)
    {
        if (IsPlaybackInfoRequest(context.Request))
        {
            CaptureDisplayRange(context.Request, store);
            await CaptureAsync(context, store).ConfigureAwait(false);
        }

        await _next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Stores the display range the web client reported in the <see cref="DisplayScript.CookieName"/>
    /// cookie. The device profile cannot tell this: browsers claim HDR support whether or not the
    /// display is in HDR mode, and tone-map to SDR themselves when it is not.
    /// </summary>
    internal static void CaptureDisplayRange(HttpRequest request, ClientCapabilityStore store)
    {
        bool? isHdr = request.Cookies[DisplayScript.CookieName] switch
        {
            "hdr" => true,
            "sdr" => false,
            _ => null
        };

        if (isHdr is not bool value)
        {
            return;
        }

        foreach (var deviceId in GetDeviceIds(request))
        {
            store.SetDisplayHdr(deviceId, value);
        }
    }

    internal static bool IsPlaybackInfoRequest(HttpRequest request)
    {
        return HttpMethods.IsPost(request.Method)
            && request.Path.HasValue
            && PlaybackInfoPathRegex().IsMatch(request.Path.Value!)
            && (request.ContentLength ?? 0) <= MaxBodyBytes
            && (request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    internal async Task CaptureAsync(HttpContext context, ClientCapabilityStore store)
    {
        var request = context.Request;
        request.EnableBuffering();
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(false);
            var capabilities = ClientVideoCapabilities.FromPlaybackInfoBody(document.RootElement);
            if (capabilities == null)
            {
                return;
            }

            foreach (var deviceId in GetDeviceIds(request))
            {
                store.Set(deviceId, capabilities);
            }
        }
        catch (Exception ex) when (ex is JsonException or OperationCanceledException or System.IO.IOException)
        {
            _logger.LogDebug(ex, "Could not read the device profile from a PlaybackInfo request.");
        }
        finally
        {
            request.Body.Position = 0;
        }
    }

    /// <summary>
    /// Device IDs as sent by the client. Some clients URL-encode header values, so both the raw
    /// and the decoded form are returned; the session reports one of them.
    /// </summary>
    internal static IEnumerable<string> GetDeviceIds(HttpRequest request)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            found.Add(value.Trim());
            var decoded = WebUtility.UrlDecode(value.Trim());
            if (!string.IsNullOrWhiteSpace(decoded))
            {
                found.Add(decoded);
            }
        }

        Add(GetAuthorizationParameter(request.Headers.Authorization.ToString(), "DeviceId"));
        Add(GetAuthorizationParameter(request.Headers["X-Emby-Authorization"].ToString(), "DeviceId"));
        Add(request.Headers["X-Emby-Device-Id"].ToString());
        Add(request.Query["deviceId"].ToString());
        return found;
    }

    internal static string? GetAuthorizationParameter(string? header, string name)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        var value = header.Trim();
        var space = value.IndexOf(' ', StringComparison.Ordinal);
        if (space > 0 && !value[..space].Contains('=', StringComparison.Ordinal))
        {
            value = value[(space + 1)..];
        }

        foreach (var part in value.Split(','))
        {
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            if (string.Equals(part[..separator].Trim(), name, StringComparison.OrdinalIgnoreCase))
            {
                return part[(separator + 1)..].Trim().Trim('"');
            }
        }

        return null;
    }
}
