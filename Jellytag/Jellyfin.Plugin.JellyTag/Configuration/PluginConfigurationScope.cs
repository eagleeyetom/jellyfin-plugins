namespace Jellyfin.Plugin.JellyTag.Configuration;

/// <summary>
/// Resolves the configuration the rendering services should use. Normally that is the saved
/// plugin configuration; the live preview temporarily overrides it for the current async flow
/// so unsaved settings can be rendered without being persisted.
/// </summary>
internal static class PluginConfigurationScope
{
    private static readonly AsyncLocal<PluginConfiguration?> OverrideValue = new();

    /// <summary>
    /// Gets the configuration in effect for the current async flow.
    /// </summary>
    public static PluginConfiguration? Current => OverrideValue.Value ?? Plugin.Instance?.Configuration;

    /// <summary>
    /// Gets a value indicating whether the current async flow renders with an override.
    /// </summary>
    public static bool IsOverridden => OverrideValue.Value != null;

    /// <summary>
    /// Uses <paramref name="configuration"/> for the current async flow until the returned scope is disposed.
    /// </summary>
    public static IDisposable Override(PluginConfiguration configuration)
    {
        var previous = OverrideValue.Value;
        OverrideValue.Value = configuration;
        return new RestoreScope(previous);
    }

    private sealed class RestoreScope : IDisposable
    {
        private readonly PluginConfiguration? _previous;
        private bool _disposed;

        public RestoreScope(PluginConfiguration? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            OverrideValue.Value = _previous;
        }
    }
}
