namespace Jellyfin.Plugin.MetadataNotifier.Middleware;

/// <summary>
/// Script injected into the web client's <c>index.html</c> that reports whether the browser's
/// display is in HDR mode. It keeps a cookie up to date, which the browser then sends with
/// every PlaybackInfo request (see <see cref="PlaybackInfoCaptureMiddleware"/>).
/// </summary>
internal static class DisplayScript
{
    /// <summary>
    /// Cookie holding "hdr" or "sdr".
    /// </summary>
    internal const string CookieName = "MetadataNotifierDisplay";

    // Browsers without the dynamic-range media feature match neither query; nothing is reported then.
    internal const string Html = $$"""
        <script>
        (function () {
            'use strict';
            if (!window.matchMedia) { return; }
            var high = window.matchMedia('(dynamic-range: high)');
            var standard = window.matchMedia('(dynamic-range: standard)');

            function update() {
                if (!high.matches && !standard.matches) { return; }
                document.cookie = '{{CookieName}}=' + (high.matches ? 'hdr' : 'sdr') + '; path=/; SameSite=Lax';
            }

            update();
            if (high.addEventListener) { high.addEventListener('change', update); }
        })();
        </script>
        """;
}
