using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Jellyfin.Plugin.MetadataNotifier.Middleware;

/// <summary>
/// Startup filter that registers the fullscreen toast fix middleware into the
/// ASP.NET Core request pipeline before Jellyfin's own middleware runs.
/// </summary>
public class MetadataNotifierStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseMiddleware<FullscreenToastMiddleware>();
            next(app);
        };
    }
}
