using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.BadgeFin.Migration;

/// <summary>
/// Handles the move from JellyTag, the name (and plugin ID) this plugin used before 2.8.0.0.
/// </summary>
/// <remarks>
/// Jellyfin stores a plugin's settings and data under its assembly name, so after the rename
/// BadgeFin would start with default settings and without the custom badges uploaded to JellyTag.
/// Both are copied (never moved), so a JellyTag install that is still present keeps working until
/// it is uninstalled.
/// </remarks>
internal static class LegacyJellyTag
{
    /// <summary>
    /// The plugin ID used by JellyTag. The original JellyTag project uses it as well.
    /// </summary>
    internal static readonly Guid PluginId = new("f4a2e8c1-9b3d-4f7a-b6c5-2d8e1a3f9b04");

    /// <summary>
    /// The assembly name JellyTag's configuration file and data folder are named after.
    /// </summary>
    internal const string AssemblyName = "Jellyfin.Plugin.JellyTag";

    /// <summary>
    /// The data subfolder holding uploaded custom badges (same name in both plugins).
    /// </summary>
    internal const string CustomBadgesFolderName = "custom-badges";

    /// <summary>
    /// Copies JellyTag's settings and custom badges to BadgeFin's locations, if BadgeFin has none yet.
    /// </summary>
    /// <param name="pluginConfigurationsPath">Jellyfin's plugin configurations folder.</param>
    /// <param name="pluginsPath">Jellyfin's plugins folder (parent of the plugin data folders).</param>
    /// <param name="configurationFilePath">BadgeFin's configuration file.</param>
    /// <param name="dataFolderPath">BadgeFin's data folder.</param>
    /// <returns>What was copied.</returns>
    internal static MigrationResult Migrate(
        string pluginConfigurationsPath,
        string pluginsPath,
        string configurationFilePath,
        string dataFolderPath)
    {
        var configurationCopied = false;
        var legacyConfigurationFile = Path.Combine(pluginConfigurationsPath, AssemblyName + ".xml");
        if (!File.Exists(configurationFilePath) && File.Exists(legacyConfigurationFile))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configurationFilePath)!);
            File.Copy(legacyConfigurationFile, configurationFilePath, overwrite: false);
            configurationCopied = true;
        }

        var badgesCopied = 0;
        var legacyBadgesFolder = Path.Combine(pluginsPath, AssemblyName, CustomBadgesFolderName);
        var badgesFolder = Path.Combine(dataFolderPath, CustomBadgesFolderName);
        if (Directory.Exists(legacyBadgesFolder) && !Directory.Exists(badgesFolder))
        {
            // Copy into a temporary folder and rename it at the end, so an interrupted copy is
            // retried on the next start instead of leaving a half-filled folder that blocks it.
            var stagingFolder = badgesFolder + ".migrating";
            if (Directory.Exists(stagingFolder))
            {
                Directory.Delete(stagingFolder, recursive: true);
            }

            Directory.CreateDirectory(stagingFolder);
            foreach (var file in Directory.EnumerateFiles(legacyBadgesFolder))
            {
                var fileName = Path.GetFileName(file);

                // Leftover temporary files of interrupted uploads start with a dot.
                if (fileName.StartsWith('.'))
                {
                    continue;
                }

                File.Copy(file, Path.Combine(stagingFolder, fileName));
                badgesCopied++;
            }

            Directory.Move(stagingFolder, badgesFolder);
        }

        return new MigrationResult(configurationCopied, badgesCopied);
    }

    /// <summary>
    /// Gets whether a JellyTag plugin (this project's old name, or the original JellyTag) is active.
    /// Both draw badges on the same images, so running it next to BadgeFin doubles the badges.
    /// </summary>
    /// <param name="plugins">The installed plugins.</param>
    /// <returns><c>true</c> if a JellyTag plugin is installed and active.</returns>
    internal static bool IsActive(IEnumerable<LocalPlugin> plugins)
    {
        return plugins.Any(p => p.Id == PluginId && p.Manifest.Status == PluginStatus.Active);
    }
}

/// <summary>
/// The outcome of <see cref="LegacyJellyTag.Migrate"/>.
/// </summary>
/// <param name="ConfigurationCopied">Whether JellyTag's settings were copied.</param>
/// <param name="CustomBadgesCopied">The number of custom badge files copied.</param>
internal readonly record struct MigrationResult(bool ConfigurationCopied, int CustomBadgesCopied);
