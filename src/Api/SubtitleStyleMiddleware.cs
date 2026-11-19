using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Plugin.PositionedSubtitles.Configuration;
using Jellyfin.Plugin.PositionedSubtitles.Services;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PositionedSubtitles.Api;

/// <summary>
/// Forces the subtitle style at delivery time, without touching any file:
///  - PlaybackInfo: for selected clients, SRT tracks (optionally only those with {\anN}) are
///    announced as "ass" with a Stream.ass URL, so the web/Tizen player renders them with libass
///    and honours positioning (its text renderer strips {\anN}).
///  - Stream.ass: SRT tracks are converted on the fly with the configured style; ASS tracks are
///    served with their white styles recoloured.
/// Any failure falls back to Jellyfin's normal response.
/// </summary>
public sealed class SubtitleStyleMiddleware
{
    private static readonly Regex PlaybackInfoPath = new(@"/Items/(?<id>[0-9a-fA-F-]{32,36})/PlaybackInfo$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SubtitlePath = new(@"/Videos/(?<item>[0-9a-fA-F-]{32,36})/(?<ms>[^/]+)/Subtitles/(?<idx>\d+)(?:/(?<ticks>\d+))?/Stream\.(?<fmt>[A-Za-z]+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex StreamExt = new(@"/Stream\.[A-Za-z]+(?=\?|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ClientField = new(@"Client=""(?<c>[^""]*)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly string[] SrtCodecs = { "subrip", "srt" };
    private static readonly string[] AssCodecs = { "ass", "ssa" };
    private static readonly ConcurrentDictionary<string, (DateTime Mtime, bool Positioned)> PositionCache = new(StringComparer.Ordinal);

    private readonly RequestDelegate _next;

    public SubtitleStyleMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        ILibraryManager libraryManager,
        IMediaEncoder mediaEncoder,
        IPathManager pathManager,
        ILocalizationManager localization,
        ILogger<SubtitleStyleMiddleware> logger)
    {
        var config = Plugin.Instance?.Configuration;
        string path = context.Request.Path.Value ?? string.Empty;
        if (config is null || !config.LiveStyling || path.Length == 0)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        bool get = HttpMethods.IsGet(context.Request.Method);
        if ((get || HttpMethods.IsPost(context.Request.Method)) && PlaybackInfoPath.IsMatch(path))
        {
            await HandlePlaybackInfo(context, config, pathManager, localization, logger).ConfigureAwait(false);
            return;
        }

        var sub = get ? SubtitlePath.Match(path) : Match.Empty;
        if (sub.Success && AssCodecs.Contains(sub.Groups["fmt"].Value, StringComparer.OrdinalIgnoreCase))
        {
            await HandleAssStream(context, sub, config, libraryManager, mediaEncoder, localization, logger).ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }

    private async Task HandlePlaybackInfo(HttpContext context, PluginConfiguration config, IPathManager pathManager, ILocalizationManager localization, ILogger logger)
    {
        string? client = GetClient(context.Request);
        var clients = config.LiveClients.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (client is null || !clients.Contains(client, StringComparer.OrdinalIgnoreCase))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var (status, body, contentType) = await CaptureAsync(context).ConfigureAwait(false);
        byte[] output = body;
        if (status == StatusCodes.Status200OK && contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            try
            {
                var root = JsonNode.Parse(body);
                int changed = Rewrite(root, config, pathManager, localization);
                if (changed > 0)
                {
                    output = Encoding.UTF8.GetBytes(root!.ToJsonString());
                    logger.LogInformation("Positioned subtitles: announced {Count} SRT track(s) as ASS to {Client}", changed, client);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Positioned subtitles: could not rewrite PlaybackInfo, passing it through");
                output = body;
            }
        }

        await WriteAsync(context, output, contentType).ConfigureAwait(false);
    }

    private static int Rewrite(JsonNode? root, PluginConfiguration config, IPathManager pathManager, ILocalizationManager localization)
    {
        var languages = LanguageMatcher.Parse(config.Languages);
        int changed = 0;
        if (root?["MediaSources"] is not JsonArray sources)
        {
            return 0;
        }

        foreach (var source in sources.OfType<JsonObject>())
        {
            string? sourceId = source["Id"]?.GetValue<string>();
            if (sourceId is null || source["MediaStreams"] is not JsonArray streams)
            {
                continue;
            }

            foreach (var s in streams.OfType<JsonObject>())
            {
                if (!string.Equals(Str(s, "Type"), "Subtitle", StringComparison.OrdinalIgnoreCase)
                    || !SrtCodecs.Contains(Str(s, "Codec") ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    || !string.Equals(Str(s, "DeliveryMethod"), "External", StringComparison.OrdinalIgnoreCase)
                    || Str(s, "DeliveryUrl") is not string url
                    || !LanguageMatcher.Matches(localization, Str(s, "Language"), languages))
                {
                    continue;
                }

                bool isExternal = s["IsExternal"]?.GetValue<bool>() == true;
                int index = s["Index"]?.GetValue<int>() ?? -1;
                if (config.OnlyPositioned && (isExternal || !HasPositioning(pathManager, sourceId, index)))
                {
                    continue;
                }

                s["Codec"] = "ass";
                s["DeliveryUrl"] = StreamExt.Replace(url, "/Stream.ass", 1);
                changed++;
            }
        }

        return changed;
    }

    private async Task HandleAssStream(HttpContext context, Match m, PluginConfiguration config, ILibraryManager libraryManager, IMediaEncoder mediaEncoder, ILocalizationManager localization, ILogger logger)
    {
        long ticks = 0;
        if (m.Groups["ticks"].Success)
        {
            long.TryParse(m.Groups["ticks"].Value, out ticks);
        }

        if (context.Request.Query.TryGetValue("startPositionTicks", out var q) && long.TryParse(q.ToString(), out long qt))
        {
            ticks = Math.Max(ticks, qt);
        }

        string sourceId = m.Groups["ms"].Value;
        int index = int.Parse(m.Groups["idx"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var item = (Guid.TryParse(sourceId, out var sid) ? libraryManager.GetItemById(sid) : null)
                   ?? libraryManager.GetItemById(Guid.Parse(m.Groups["item"].Value));
        var stream = item?.GetMediaStreams().FirstOrDefault(s => s.Type == MediaStreamType.Subtitle && s.Index == index);
        var languages = LanguageMatcher.Parse(config.Languages);
        if (item is null || stream is null || ticks != 0 || !LanguageMatcher.Matches(localization, stream.Language, languages))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        string styleLine = AssBuilder.BuildStyleLine(config);
        string hash = AssBuilder.StyleHash(styleLine);
        string codec = stream.Codec ?? string.Empty;
        string mime = MimeTypes.GetMimeType("file.ass") ?? "text/x-ssa";

        if (SrtCodecs.Contains(codec, StringComparer.OrdinalIgnoreCase))
        {
            // Let Jellyfin itself serve the SRT (this keeps its authentication, authorisation and
            // stream-index handling), then convert the authorised response.
            string originalPath = context.Request.Path.Value!;
            context.Request.Path = new PathString(StreamExt.Replace(originalPath, "/Stream.srt", 1));
            var (srtStatus, srtBody, srtType) = await CaptureAsync(context).ConfigureAwait(false);
            if (srtStatus != StatusCodes.Status200OK)
            {
                await WriteAsync(context, srtBody, srtType).ConfigureAwait(false);
                return;
            }

            try
            {
                string srt = Encoding.UTF8.GetString(srtBody);
                string ass = AssBuilder.Patch(await AssBuilder.ConvertTextAsync(mediaEncoder.EncoderPath, srt, context.RequestAborted).ConfigureAwait(false), styleLine, hash);
                if (AssBuilder.CountDialogues(ass) == 0 && AssBuilder.CountSrtCues(srt) > 0)
                {
                    throw new InvalidOperationException("conversion produced no dialogue");
                }

                logger.LogDebug("Positioned subtitles: served {Path} stream {Index} as styled ASS", item.Path, index);
                await WriteAsync(context, Encoding.UTF8.GetBytes(ass), mime, StatusCodes.Status200OK).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The client expects ASS; an SRT body would fail in libass, so return an error it can show.
                logger.LogWarning(ex, "Positioned subtitles: on-the-fly conversion failed for {Path} stream {Index}", item.Path, index);
                if (!context.Response.HasStarted)
                {
                    context.Response.Clear();
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                }

                return;
            }
        }

        if (!AssCodecs.Contains(codec, StringComparer.OrdinalIgnoreCase) || !config.RestyleEmbeddedAss)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var (status, body, contentType) = await CaptureAsync(context).ConfigureAwait(false);
        byte[] output = body;
        if (status == StatusCodes.Status200OK)
        {
            try
            {
                string text = Encoding.UTF8.GetString(body);
                string restyled = AssBuilder.Restyle(text, config.PrimaryColor, hash, out int changedStyles);
                if (changedStyles > 0 && AssBuilder.CountDialogues(restyled) == AssBuilder.CountDialogues(text))
                {
                    output = Encoding.UTF8.GetBytes(restyled);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Positioned subtitles: could not recolour {Path} stream {Index}", item.Path, index);
            }
        }

        await WriteAsync(context, output, contentType).ConfigureAwait(false);
    }

    private static bool HasPositioning(IPathManager pathManager, string sourceId, int index)
    {
        string? path = pathManager.GetSubtitlePath(sourceId, index, ".srt");
        if (path is null || !File.Exists(path))
        {
            return false;
        }

        DateTime mtime = File.GetLastWriteTimeUtc(path);
        if (PositionCache.TryGetValue(path, out var cached) && cached.Mtime == mtime)
        {
            return cached.Positioned;
        }

        bool positioned = AssBuilder.PositionTag.IsMatch(File.ReadAllText(path));
        if (PositionCache.Count > 20000)
        {
            PositionCache.Clear();
        }

        PositionCache[path] = (mtime, positioned);
        return positioned;
    }

    /// <summary>Runs the rest of the pipeline uncompressed and without validators, capturing the body.</summary>
    private async Task<(int Status, byte[] Body, string? ContentType)> CaptureAsync(HttpContext context)
    {
        context.Request.Headers.Remove("Accept-Encoding");
        context.Request.Headers.Remove("If-None-Match");
        context.Request.Headers.Remove("If-Modified-Since");
        context.Request.Headers.Remove("Range");

        Stream original = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = original;
        }

        return (context.Response.StatusCode, buffer.ToArray(), context.Response.ContentType);
    }

    private static async Task WriteAsync(HttpContext context, byte[] body, string? contentType, int? status = null)
    {
        if (status is int s)
        {
            context.Response.StatusCode = s;
        }

        if (contentType is not null)
        {
            context.Response.ContentType = contentType;
        }

        context.Response.Headers.Remove("ETag");
        context.Response.Headers.Remove("Last-Modified");
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.ContentLength = body.Length;
        await context.Response.Body.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
    }

    private static string? GetClient(HttpRequest request)
    {
        foreach (string header in new[] { "Authorization", "X-Emby-Authorization" })
        {
            if (request.Headers.TryGetValue(header, out var value))
            {
                var m = ClientField.Match(value.ToString());
                if (m.Success)
                {
                    return Uri.UnescapeDataString(m.Groups["c"].Value);
                }
            }
        }

        return request.Query.TryGetValue("client", out var c) ? c.ToString() : null;
    }

    private static string? Str(JsonObject o, string key)
    {
        return o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }
}
