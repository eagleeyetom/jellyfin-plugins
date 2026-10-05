using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyTag.Configuration;
using Jellyfin.Plugin.JellyTag.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyTag.Controllers;

/// <summary>
/// Controller for JellyTag plugin admin and debug endpoints.
/// </summary>
[ApiController]
[Route("JellyTag")]
public partial class JellyTagController : ControllerBase
{
    private readonly IImageCacheService _cacheService;
    private readonly IImageOverlayService _overlayService;
    private readonly IQualityDetectionService _qualityService;

    private static readonly string[] SupportedBadgeExtensions = { ".svg", ".png", ".jpg", ".jpeg" };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly string[] ManifestResourceNamesArray = Assembly.GetExecutingAssembly().GetManifestResourceNames();
    private static readonly HashSet<string> ManifestResourceNames = new(ManifestResourceNamesArray, StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"^[a-zA-Z0-9._-]+$")]
    private static partial Regex SafeBadgeKeyRegex();

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyTagController"/> class.
    /// </summary>
    public JellyTagController(IImageCacheService cacheService, IImageOverlayService overlayService, IQualityDetectionService qualityService)
    {
        _cacheService = cacheService;
        _overlayService = overlayService;
        _qualityService = qualityService;
    }

    /// <summary>
    /// Gets whether the plugin is running in Debug configuration.
    /// </summary>
    [HttpGet("IsDebug")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetIsDebug()
    {
#if DEBUG
        return Ok(true);
#else
        return Ok(false);
#endif
    }

    /// <summary>
    /// Clears the image cache.
    /// </summary>
    [HttpPost("ClearCache")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult ClearCache()
    {
        _cacheService.ClearCache();
        _qualityService.ClearBadgeCache();
        _overlayService.ReloadBadges();
        return NoContent();
    }

    /// <summary>
    /// Gets cache statistics.
    /// </summary>
    [HttpGet("CacheStats")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetCacheStats()
    {
        var stats = _cacheService.GetCacheStats();
        return Ok(new
        {
            FileCount = stats.FileCount,
            TotalSizeMB = Math.Round(stats.TotalSizeBytes / (1024.0 * 1024.0), 2),
            OldestEntry = stats.OldestEntry,
            NewestEntry = stats.NewestEntry
        });
    }

    /// <summary>
    /// Gets the plugin status.
    /// </summary>
    [HttpGet("Status")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetStatus()
    {
        var config = Plugin.Instance?.Configuration;
        return Ok(new
        {
            Enabled = config?.Enabled ?? false,
            PosterEnabled = config?.PosterConfig?.Enabled ?? false,
            ThumbnailEnabled = config?.ThumbnailConfig?.Enabled ?? false,
            ThumbnailSameAsPoster = config?.ThumbnailSameAsPoster ?? false,
            OutputFormat = config?.OutputFormat.ToString() ?? "Jpeg"
        });
    }

    /// <summary>
    /// Debug endpoint to list embedded resources.
    /// </summary>
    [HttpGet("Debug/Resources")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetResources()
    {
        return Ok(new
        {
            AssemblyName = typeof(JellyTagController).Assembly.FullName,
            Resources = ManifestResourceNamesArray
        });
    }

    /// <summary>
    /// Debug endpoint to get a raw badge image.
    /// </summary>
    [HttpGet("Debug/Badge/{quality}")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetBadge(string quality)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceNames = ManifestResourceNames;

        var fileName = quality.ToLower() switch
        {
            "4k" => "badge-4k.svg",
            "1080p" => "badge-1080p.svg",
            "720p" => "badge-720p.svg",
            "sd" => "badge-sd.svg",
            "hdr10" => "badge-hdr10.svg",
            "hdr10plus" => "badge-hdr10plus.svg",
            "dv" => "badge-dv.svg",
            "hlg" => "badge-hlg.svg",
            "atmos" => "badge-atmos.svg",
            "dtsx" => "badge-dtsx.svg",
            "truehd" => "badge-truehd.svg",
            "dtshdma" => "badge-dtshdma.svg",
            "opus" => "badge-opus.svg",
            "flac" => "badge-flac.svg",
            "dts" => "badge-dts.svg",
            "eac3" => "badge-eac3.svg",
            "ac3" => "badge-ac3.svg",
            "aac" => "badge-aac.svg",
            "5.1" => "badge-5_1.svg",
            "7.1" => "badge-7_1.svg",
            "stereo" => "badge-stereo.svg",
            "mono" => "badge-mono.svg",
            "3d" => "badge-3d.svg",
            "hdr" => "badge-hdr.svg",
            "h264" => "badge-h264.svg",
            "hevc" => "badge-hevc.svg",
            "av1" => "badge-av1.svg",
            "vp9" => "badge-vp9.svg",
            "mpeg2" => "badge-mpeg2.svg",
            "vc1" => "badge-vc1.svg",
            _ => null
        };

        if (fileName == null)
            return NotFound("Invalid quality");

        var resourceName = resourceNames.FirstOrDefault(r => r.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
        if (resourceName == null)
            return NotFound($"Resource not found: {fileName}");

        var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            return NotFound("Stream is null");

        var contentType = fileName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? "image/svg+xml" : "image/png";
        return File(stream, contentType);
    }

    /// <summary>
    /// Uploads a custom badge to override the default badge for a given key.
    /// Accepts PNG, JPEG, and SVG files.
    /// </summary>
    [HttpPost("CustomBadge/{badgeKey}")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UploadCustomBadge(string badgeKey, IFormFile file, [FromQuery] bool logo = false)
    {
        if (!SafeBadgeKeyRegex().IsMatch(badgeKey))
        {
            return BadRequest("Invalid badge key");
        }

        if (file == null || file.Length == 0)
        {
            return BadRequest("No file uploaded");
        }

        const long maxFileSize = 5 * 1024 * 1024; // 5 MB
        if (file.Length > maxFileSize)
        {
            return BadRequest("File too large. Maximum size is 5 MB.");
        }

        var extension = file.ContentType.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/svg+xml" => ".svg",
            _ => null
        };

        if (extension == null)
        {
            return BadRequest("Only PNG, JPEG, and SVG files are accepted");
        }

        var dataFolder = Plugin.Instance?.DataFolderPath;
        if (string.IsNullOrEmpty(dataFolder))
        {
            return BadRequest("Plugin data folder not available");
        }

        var customDir = Path.Combine(dataFolder, "custom-badges");
        Directory.CreateDirectory(customDir);

        var fileKey = badgeKey.Replace('.', '_');
        var prefix = ResolveAssetPrefix(fileKey, logo);
        var fileName = $"{prefix}{fileKey}{extension}";
        var filePath = Path.Combine(customDir, fileName);
        var tempPath = Path.Combine(customDir, $".{fileName}.{Guid.NewGuid():N}.tmp");
        var backups = new List<(string Original, string Backup)>();

        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                await file.CopyToAsync(stream).ConfigureAwait(false);
            }

            try
            {
                foreach (var ext in SupportedBadgeExtensions)
                {
                    var existing = Path.Combine(customDir, $"{prefix}{fileKey}{ext}");
                    if (!System.IO.File.Exists(existing))
                    {
                        continue;
                    }

                    var backup = existing + $".{Guid.NewGuid():N}.bak";
                    System.IO.File.Move(existing, backup);
                    backups.Add((existing, backup));
                }

                System.IO.File.Move(tempPath, filePath);
            }
            catch (Exception replacementException)
            {
                var rollbackErrors = new List<Exception>();
                foreach (var (original, backup) in backups.AsEnumerable().Reverse())
                {
                    try
                    {
                        if (System.IO.File.Exists(backup) && !System.IO.File.Exists(original))
                        {
                            System.IO.File.Move(backup, original);
                        }
                    }
                    catch (Exception rollbackException)
                    {
                        rollbackErrors.Add(rollbackException);
                    }
                }

                if (rollbackErrors.Count > 0)
                {
                    throw new AggregateException("Custom badge replacement failed and rollback was incomplete.",
                        new[] { replacementException }.Concat(rollbackErrors));
                }

                throw;
            }

            foreach (var (_, backup) in backups)
            {
                try
                {
                    System.IO.File.Delete(backup);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        finally
        {
            try
            {
                System.IO.File.Delete(tempPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        // Reload badges and clear cache
        _overlayService.ReloadBadges();
        _cacheService.ClearCache();

        return NoContent();
    }

    /// <summary>
    /// Deletes a custom badge override, reverting to the default embedded badge.
    /// </summary>
    [HttpDelete("CustomBadge/{badgeKey}")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult DeleteCustomBadge(string badgeKey, [FromQuery] bool logo = false)
    {
        if (!SafeBadgeKeyRegex().IsMatch(badgeKey))
        {
            return BadRequest("Invalid badge key");
        }

        var dataFolder = Plugin.Instance?.DataFolderPath;
        if (string.IsNullOrEmpty(dataFolder))
        {
            return NotFound();
        }

        var fileKey = badgeKey.Replace('.', '_');
        var customDir = Path.Combine(dataFolder, "custom-badges");
        var prefix = ResolveAssetPrefix(fileKey, logo);
        var found = false;

        foreach (var ext in SupportedBadgeExtensions)
        {
            var filePath = Path.Combine(customDir, $"{prefix}{fileKey}{ext}");
            if (System.IO.File.Exists(filePath))
            {
                System.IO.File.Delete(filePath);
                found = true;
            }
        }

        if (!found)
        {
            return NotFound("Custom badge not found");
        }

        _overlayService.ReloadBadges();
        _cacheService.ClearCache();

        return NoContent();
    }

    /// <summary>
    /// Lists all custom badge overrides.
    /// </summary>
    [HttpGet("CustomBadges")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetCustomBadges([FromQuery] bool logo = false)
    {
        var dataFolder = Plugin.Instance?.DataFolderPath;
        if (string.IsNullOrEmpty(dataFolder))
        {
            return Ok(Array.Empty<string>());
        }

        var customDir = Path.Combine(dataFolder, "custom-badges");
        if (!Directory.Exists(customDir))
        {
            return Ok(Array.Empty<string>());
        }

        var prefix = logo ? "logo-" : "badge-";
        var files = Directory.GetFiles(customDir, prefix + "*.*")
            .Where(f => SupportedBadgeExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Select(f => Path.GetFileNameWithoutExtension(f).Replace(prefix, string.Empty).Replace('_', '.'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Ok(files);
    }

    /// <summary>
    /// Resolves which asset family a preview or override applies to. The renderer uses
    /// logo-{key} when a panel is in Brand Logo style and a logo is bundled for that badge,
    /// so previews and uploads have to follow it or they silently target the wrong file.
    /// </summary>
    private static string ResolveAssetPrefix(string fileKey, bool logoStyle)
    {
        if (!logoStyle)
        {
            return "badge-";
        }

        var hasLogo = ManifestResourceNames.Any(r =>
            r.EndsWith($".logo-{fileKey}.svg", StringComparison.OrdinalIgnoreCase) ||
            r.EndsWith($".logo-{fileKey}.png", StringComparison.OrdinalIgnoreCase));

        return hasLogo ? "logo-" : "badge-";
    }

    /// <summary>
    /// Serves a badge preview image. Returns custom badge if present, otherwise embedded default.
    /// Order: custom (svg > png > jpg) → embedded (svg > png).
    /// </summary>
    [HttpGet("BadgePreview/{badgeKey}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetBadgePreview(string badgeKey, [FromQuery] bool logo = false, [FromQuery] bool? white = null, [FromQuery] string? bgColor = null, [FromQuery] string? textColor = null, [FromQuery] bool? origAac = null)
    {
        if (!SafeBadgeKeyRegex().IsMatch(badgeKey))
        {
            return BadRequest("Invalid badge key");
        }

        var effectiveBg = bgColor ?? Plugin.Instance?.Configuration?.CustomBadgeColors?
            .FirstOrDefault(c => string.Equals(c.BadgeKey, badgeKey, StringComparison.OrdinalIgnoreCase))?.BgColor;
        var effectiveText = textColor ?? Plugin.Instance?.Configuration?.CustomBadgeColors?
            .FirstOrDefault(c => string.Equals(c.BadgeKey, badgeKey, StringComparison.OrdinalIgnoreCase))?.TextColor;
        var hasColorOverride = !string.IsNullOrEmpty(effectiveBg) || !string.IsNullOrEmpty(effectiveText);

        var useOrigAac = badgeKey.Equals("aac", StringComparison.OrdinalIgnoreCase)
            && (origAac ?? Plugin.Instance?.Configuration?.UseOriginalAacLogo == true);
        var aacSuffix = (logo && useOrigAac) ? "-orig" : "";

        // Normalize dots to underscores for file lookup (e.g. "5.1" -> "5_1")
        var fileKey = badgeKey.Replace('.', '_') + aacSuffix;
        var isWhite = white ?? Plugin.Instance?.Configuration?.WhiteLogoBackground == true;
        if (isWhite)
        {
            var whiteKey = fileKey + "-white";
            var whitePrefix = ResolveAssetPrefix(whiteKey, logo);
            var customDirCheck = !string.IsNullOrEmpty(Plugin.Instance?.DataFolderPath)
                ? Path.Combine(Plugin.Instance.DataFolderPath, "custom-badges")
                : null;
            var hasWhiteCustom = customDirCheck != null && SupportedBadgeExtensions.Any(ext =>
                System.IO.File.Exists(Path.Combine(customDirCheck, $"{whitePrefix}{whiteKey}{ext}")));
            var hasWhiteEmbedded = ManifestResourceNames.Any(r =>
                r.EndsWith($"{whitePrefix}{whiteKey}.svg", StringComparison.OrdinalIgnoreCase) ||
                r.EndsWith($"{whitePrefix}{whiteKey}.png", StringComparison.OrdinalIgnoreCase));

            if (hasWhiteCustom || hasWhiteEmbedded)
            {
                fileKey = whiteKey;
            }
        }

        var prefix = ResolveAssetPrefix(fileKey, logo);

        // Check custom badges first: SVG > PNG > JPG > JPEG
        var dataFolder = Plugin.Instance?.DataFolderPath;
        if (!string.IsNullOrEmpty(dataFolder))
        {
            var customDir = Path.Combine(dataFolder, "custom-badges");
            foreach (var ext in SupportedBadgeExtensions)
            {
                var customPath = Path.Combine(customDir, $"{prefix}{fileKey}{ext}");
                if (System.IO.File.Exists(customPath))
                {
                    if (ext == ".svg" && hasColorOverride)
                    {
                        var rawSvg = System.IO.File.ReadAllBytes(customPath);
                        var recolored = ImageOverlayService.RecolorSvg(rawSvg, effectiveBg, effectiveText, badgeKey);
                        return File(recolored, "image/svg+xml");
                    }

                    var ct = ext switch
                    {
                        ".svg" => "image/svg+xml",
                        ".png" => "image/png",
                        _ => "image/jpeg"
                    };
                    return PhysicalFile(customPath, ct);
                }
            }
        }

        // Fall back to embedded resources: SVG > PNG
        var assembly = Assembly.GetExecutingAssembly();

        // If recoloring is requested, prefer SVG even if logo resolved to PNG (e.g. badge-dtsx.svg)
        var targetSvgPrefix = prefix;
        var targetSvgFileKey = fileKey;
        if (hasColorOverride)
        {
            var svgExists = ManifestResourceNames.Any(r => r.EndsWith($"{prefix}{fileKey}.svg", StringComparison.OrdinalIgnoreCase));
            if (!svgExists)
            {
                targetSvgPrefix = "badge-";
                targetSvgFileKey = badgeKey.Replace('.', '_');
            }
        }

        // Try SVG first
        var svgResourceName = ManifestResourceNames
            .FirstOrDefault(r => r.EndsWith($"{targetSvgPrefix}{targetSvgFileKey}.svg", StringComparison.OrdinalIgnoreCase));
        if (svgResourceName != null)
        {
            using var stream = assembly.GetManifestResourceStream(svgResourceName);
            if (stream != null)
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                var svgBytes = ms.ToArray();
                if (hasColorOverride)
                {
                    svgBytes = ImageOverlayService.RecolorSvg(svgBytes, effectiveBg, effectiveText, badgeKey);
                }
                return File(svgBytes, "image/svg+xml");
            }
        }

        // Then PNG
        var pngResourceName = ManifestResourceNames
            .FirstOrDefault(r => r.EndsWith($"{prefix}{fileKey}.png", StringComparison.OrdinalIgnoreCase));
        if (pngResourceName != null)
        {
            var stream = assembly.GetManifestResourceStream(pngResourceName);
            if (stream != null)
            {
                return File(stream, "image/png");
            }
        }

        return NotFound();
    }

    /// <summary>
    /// Exports the plugin configuration as a JSON file.
    /// </summary>
    [HttpGet("ExportConfig")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult ExportConfig()
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return BadRequest("Plugin not loaded");
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(plugin.Configuration, JsonOptions);
        return File(json, "application/json", "jellytag-config.json");
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
            _cacheService.ClearCache();
            _qualityService.ClearBadgeCache();
            _overlayService.ReloadBadges();

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
    public IActionResult ResetConfig()
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return BadRequest("Plugin not loaded");
        }

        plugin.UpdateConfiguration(new Configuration.PluginConfiguration());
        _cacheService.ClearCache();
        _qualityService.ClearBadgeCache();
        _overlayService.ReloadBadges();

        return NoContent();
    }
}
