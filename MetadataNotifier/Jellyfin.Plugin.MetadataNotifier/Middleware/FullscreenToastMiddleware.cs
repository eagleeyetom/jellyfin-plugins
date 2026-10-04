using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetadataNotifier.Middleware;

/// <summary>
/// Middleware that intercepts Jellyfin Web's <c>index.html</c> response and injects a
/// persistent fullscreen toast fix script into it.
///
/// <para>
/// Problem: Jellyfin Web appends <c>.toastContainer</c> directly to <c>document.body</c>.
/// When the video player calls <c>element.requestFullscreen()</c>, the browser moves only
/// that element's subtree into the fullscreen "top layer", hiding everything else on
/// <c>document.body</c> — including the toast container. This is why toasts appear on the
/// very first play (before fullscreen is entered) but go invisible on every subsequent play.
/// </para>
///
/// <para>
/// Fix: Inject a small script into <c>index.html</c> that listens for
/// <c>fullscreenchange</c> events and moves <c>.toastContainer</c> into the active fullscreen
/// element (and back to <c>body</c> when fullscreen exits). A <c>MutationObserver</c> on
/// <c>document.body</c> handles the case where the container has not been created yet.
/// </para>
/// </summary>
public class FullscreenToastMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<FullscreenToastMiddleware> _logger;

    // The script is inlined to avoid an extra HTTP round-trip and to keep the plugin
    // self-contained with no external file dependencies.
    private const string InjectedScript = """
        <script>
        (function () {
            'use strict';

            var applied = false;

            function syncToastContainer() {
                var container = document.querySelector('.toastContainer');
                if (!container) { return; }
                var fsEl = document.fullscreenElement || document.webkitFullscreenElement || null;
                if (fsEl && container.parentNode !== fsEl) {
                    fsEl.appendChild(container);
                } else if (!fsEl && container.parentNode !== document.body) {
                    document.body.appendChild(container);
                }
            }

            function applyFix() {
                if (applied) { return; }
                applied = true;

                document.addEventListener('fullscreenchange', syncToastContainer);
                document.addEventListener('webkitfullscreenchange', syncToastContainer);

                // Watch for .toastContainer being created (first toast of a session).
                var bodyObserver = new MutationObserver(function (mutations) {
                    for (var i = 0; i < mutations.length; i++) {
                        var added = mutations[i].addedNodes;
                        for (var j = 0; j < added.length; j++) {
                            if (added[j].nodeType === 1 && added[j].classList && added[j].classList.contains('toastContainer')) {
                                syncToastContainer();
                            }
                        }
                    }
                });
                bodyObserver.observe(document.body, { childList: true });

                // Handle already-existing container (rare, but possible on re-navigation).
                syncToastContainer();
            }

            // Apply as soon as <body> is available.
            if (document.body) {
                applyFix();
            } else {
                document.addEventListener('DOMContentLoaded', applyFix);
            }
        })();
        </script>
        """;

    // Injected immediately before </body> so it runs after the Jellyfin SPA bootstraps.
    private const string InjectionMarker = "</body>";

    /// <summary>
    /// Initializes a new instance of the <see cref="FullscreenToastMiddleware"/> class.
    /// </summary>
    public FullscreenToastMiddleware(RequestDelegate next, ILogger<FullscreenToastMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Processes the HTTP request. Intercepts <c>index.html</c> GET responses and
    /// injects the fullscreen toast fix script when the option is enabled in plugin config.
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        // Skip entirely when the option is disabled (default).
        if (Plugin.Instance?.Configuration.EnableFullscreenToastFix != true)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var path = context.Request.Path.Value ?? string.Empty;

        // Only intercept the web-client root index.html (served at "/" or "/index.html").
        bool isIndexHtml = string.Equals(path, "/", System.StringComparison.Ordinal)
            || string.Equals(path, "/index.html", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, "/web/index.html", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, "/web/", System.StringComparison.Ordinal);

        if (!isIndexHtml || !string.Equals(context.Request.Method, "GET", System.StringComparison.OrdinalIgnoreCase))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Strip Accept-Encoding so downstream response compression does not compress index.html before we can inject our script.
        context.Request.Headers.Remove("Accept-Encoding");

        // Buffer the original response so we can manipulate it.
        var originalBody = context.Response.Body;
        using var buffered = new MemoryStream();
        context.Response.Body = buffered;

        try
        {
            await _next(context).ConfigureAwait(false);

            // Only patch successful (200 OK), uncompressed HTML responses.
            var contentType = context.Response.ContentType ?? string.Empty;
            var hasEncoding = !string.IsNullOrEmpty(context.Response.Headers.ContentEncoding)
                || context.Response.Headers.ContainsKey("Content-Encoding");

            if (context.Response.StatusCode != 200
                || hasEncoding
                || buffered.Length == 0
                || !contentType.Contains("text/html", System.StringComparison.OrdinalIgnoreCase))
            {
                buffered.Seek(0, SeekOrigin.Begin);
                await buffered.CopyToAsync(originalBody).ConfigureAwait(false);
                return;
            }

            buffered.Seek(0, SeekOrigin.Begin);
            using var reader = new StreamReader(buffered, Encoding.UTF8, leaveOpen: true);
            var html = await reader.ReadToEndAsync().ConfigureAwait(false);

            int markerIndex = html.LastIndexOf(InjectionMarker, System.StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                // Marker not found — serve the original response unchanged.
                _logger.LogWarning("FullscreenToastMiddleware: could not find </body> in index.html; skipping injection.");
                buffered.Seek(0, SeekOrigin.Begin);
                await buffered.CopyToAsync(originalBody).ConfigureAwait(false);
                return;
            }

            var patched = string.Concat(
                html.AsSpan(0, markerIndex),
                InjectedScript,
                html.AsSpan(markerIndex));

            var patchedBytes = Encoding.UTF8.GetBytes(patched);
            context.Response.ContentLength = patchedBytes.Length;

            await originalBody.WriteAsync(patchedBytes).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }
}
