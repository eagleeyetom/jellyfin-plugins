using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.MetadataNotifier.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MetadataNotifier.Controllers;

/// <summary>
/// Controller for Metadata Notifier plugin admin endpoints (export, import, reset).
/// </summary>
[ApiController]
[Route("MetadataNotifier")]
public class MetadataNotifierController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Exports the plugin configuration as a JSON file.
    /// </summary>
    [HttpGet("ExportConfig")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult ExportConfig()
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return BadRequest("Plugin not loaded");
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(plugin.Configuration, JsonOptions);
        return File(json, "application/json", "metadata-notifier-config.json");
    }

    /// <summary>
    /// Imports a plugin configuration from a JSON file.
    /// </summary>
    [HttpPost("ImportConfig")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ImportConfig(IFormFile file)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return BadRequest("Plugin not loaded");
        }

        if (file == null || file.Length == 0)
        {
            return BadRequest("No file uploaded");
        }

        const long maxFileSize = 1 * 1024 * 1024; // 1 MB
        if (file.Length > maxFileSize)
        {
            return BadRequest("File too large. Maximum size is 1 MB.");
        }

        try
        {
            using var stream = file.OpenReadStream();
            var imported = await JsonSerializer.DeserializeAsync<PluginConfiguration>(stream, JsonOptions).ConfigureAwait(false);
            if (imported == null)
            {
                return BadRequest("Invalid configuration file");
            }

            plugin.UpdateConfiguration(imported);
            return NoContent();
        }
        catch (JsonException)
        {
            return BadRequest("Invalid JSON format");
        }
    }

    /// <summary>
    /// Resets all plugin configuration to defaults.
    /// </summary>
    [HttpPost("ResetConfig")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult ResetConfig()
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return BadRequest("Plugin not loaded");
        }

        plugin.UpdateConfiguration(new PluginConfiguration());
        return NoContent();
    }
}
