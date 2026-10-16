// Shared between JellyTag and Metadata Notifier. This file is compiled into each plugin
// (see the <Compile Include="..\..\Shared\..." Link="..."> items in both .csproj files) so that
// each plugin still ships as a single self-contained DLL. Keep everything here internal and free
// of plugin-specific types, and change it in one place only.
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Shared.MediaDetection;

/// <summary>
/// Pure helpers that decide whether a video stream carries Dolby Vision or HDR10+ metadata,
/// from Jellyfin's probe results, stream metadata text, and (as a last resort) the file name.
/// </summary>
internal static partial class HdrFormatDetection
{
    // "DV", "DOVI" or "Dolby Vision" as a separate token, so "DVD", "DVDRip" and "DVB-T" do not match.
    [GeneratedRegex(
        @"(?:[\.\-_\(\[](?:DV|DOVI|Dolby[\.\-_ ]?Vision)[\.\-_\)\]]|\b(?:DV|DOVI|Dolby[\.\-_ ]?Vision)\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex DolbyVisionTokenRegex();

    // "HDR10+", "HDR10Plus", "HDR10.Plus", ... as a separate token, so plain "HDR10" does not match.
    [GeneratedRegex(
        @"(?:[\\/\s\.\-_\[\(]|^)HDR10[\.\-_ ]?(?:\+|Plus)(?=[\\/\s\.\-_\]\)]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex Hdr10PlusTokenRegex();

    internal static bool IsDolbyVisionRangeType(VideoRangeType rangeType)
    {
        return rangeType is VideoRangeType.DOVI
            or VideoRangeType.DOVIWithHDR10
            or VideoRangeType.DOVIWithHLG
            or VideoRangeType.DOVIWithSDR
            or VideoRangeType.DOVIWithEL
            or VideoRangeType.DOVIWithHDR10Plus
            or VideoRangeType.DOVIWithELHDR10Plus;
    }

    internal static bool IsHdr10PlusRangeType(VideoRangeType rangeType)
    {
        return rangeType is VideoRangeType.HDR10Plus
            or VideoRangeType.DOVIWithHDR10Plus
            or VideoRangeType.DOVIWithELHDR10Plus;
    }

    internal static bool IsDolbyVision(MediaStream? videoStream, string? itemPath, string? itemName)
    {
        if (videoStream == null)
        {
            return false;
        }

        return IsDolbyVision(
            videoStream.VideoRangeType,
            videoStream.DvProfile,
            videoStream.Profile,
            videoStream.Title,
            videoStream.DisplayTitle,
            itemPath,
            itemName);
    }

    /// <summary>
    /// Checks, in order: the probed DV profile, the probed range type, stream metadata text,
    /// the file name (parent folder as fallback) and the item name.
    /// </summary>
    internal static bool IsDolbyVision(
        VideoRangeType rangeType,
        int? dvProfile,
        string? profile,
        string? title,
        string? displayTitle,
        string? itemPath,
        string? itemName)
    {
        if (dvProfile > 0 || IsDolbyVisionRangeType(rangeType))
        {
            return true;
        }

        if (StreamMetadataIndicatesDolbyVision(profile, title, displayTitle))
        {
            return true;
        }

        return PathIndicatesDolbyVision(itemPath)
            || (!string.IsNullOrEmpty(itemName) && DolbyVisionTokenRegex().IsMatch(itemName));
    }

    internal static bool IsHdr10Plus(MediaStream? videoStream, string? itemPath, string? itemName)
    {
        if (videoStream == null)
        {
            return false;
        }

        return IsHdr10Plus(
            videoStream.VideoRangeType,
            videoStream.Hdr10PlusPresentFlag,
            videoStream.Profile,
            videoStream.Title,
            videoStream.DisplayTitle,
            videoStream.Comment,
            itemPath,
            itemName);
    }

    /// <summary>
    /// Checks, in order: the probed HDR10+ flag, the probed range type, stream metadata text,
    /// the file name (parent folder as fallback) and the item name.
    /// </summary>
    internal static bool IsHdr10Plus(
        VideoRangeType rangeType,
        bool? hdr10PlusPresentFlag,
        string? profile,
        string? title,
        string? displayTitle,
        string? comment,
        string? itemPath,
        string? itemName)
    {
        if (hdr10PlusPresentFlag == true || IsHdr10PlusRangeType(rangeType))
        {
            return true;
        }

        if (StreamMetadataIndicatesHdr10Plus(profile, title, displayTitle, comment))
        {
            return true;
        }

        return PathIndicatesHdr10Plus(itemPath) || TextIndicatesHdr10Plus(itemName);
    }

    internal static bool StreamMetadataIndicatesDolbyVision(string? profile, string? title, string? displayTitle)
    {
        var combined = $"{profile} {title} {displayTitle}";

        // Stream metadata describes this very stream, so the plain "DOVI" substring (used by
        // Metadata Notifier, e.g. "DOVIWithHDR10") is kept alongside the token regex (used by JellyTag).
        return combined.Contains("DOVI", StringComparison.OrdinalIgnoreCase)
            || DolbyVisionTokenRegex().IsMatch(combined);
    }

    internal static bool StreamMetadataIndicatesHdr10Plus(string? profile, string? title, string? displayTitle, string? comment)
    {
        var combined = $"{profile} {title} {displayTitle} {comment}";
        return combined.Contains("HDR10+", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("HDR10 PLUS", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("HDR10PLUS", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("ST 2094-40", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("SMPTE ST 2094", StringComparison.OrdinalIgnoreCase)
            || Hdr10PlusTokenRegex().IsMatch(combined);
    }

    /// <summary>
    /// Looks for a Dolby Vision token in the file name, falling back to the immediate parent folder
    /// (release folders often carry the tags while the file inside has a short or obfuscated name).
    /// Higher folders are never inspected, so a library folder such as "DV Movies" does not mark
    /// every file below it.
    /// </summary>
    internal static bool PathIndicatesDolbyVision(string? path)
    {
        var (fileName, parentFolder) = GetPathNames(path);
        return DolbyVisionTokenRegex().IsMatch(fileName) || DolbyVisionTokenRegex().IsMatch(parentFolder);
    }

    /// <summary>
    /// Looks for an HDR10+ token in the file name, falling back to the immediate parent folder.
    /// See <see cref="PathIndicatesDolbyVision"/>.
    /// </summary>
    internal static bool PathIndicatesHdr10Plus(string? path)
    {
        var (fileName, parentFolder) = GetPathNames(path);
        return TextIndicatesHdr10Plus(fileName) || TextIndicatesHdr10Plus(parentFolder);
    }

    /// <summary>
    /// Returns the part of a media path that generic, substring-based HDR checks ("HDR", "HDR10",
    /// "HLG") may look at: the file name only. Matching those loose substrings against the whole
    /// path (both plugins did this before) marked everything under a folder like "4K HDR" as HDR.
    /// </summary>
    internal static string GetFileNameForDetection(string? path)
    {
        return GetPathNames(path).FileName;
    }

    /// <summary>
    /// Splits a media path into its last segment (the file name) and the segment before it (the
    /// parent folder name). Both '/' and '\' are treated as separators regardless of the host OS,
    /// and a trailing ".dv" extension (raw DV camcorder video) is dropped so it is not read as a
    /// Dolby Vision tag.
    /// </summary>
    internal static (string FileName, string ParentFolder) GetPathNames(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return (string.Empty, string.Empty);
        }

        var trimmed = path.TrimEnd('/', '\\');
        var fileStart = trimmed.LastIndexOfAny(['/', '\\']) + 1;
        var fileName = trimmed[fileStart..];
        if (fileName.EndsWith(".dv", StringComparison.OrdinalIgnoreCase))
        {
            fileName = fileName[..^3];
        }

        if (fileStart == 0)
        {
            return (fileName, string.Empty);
        }

        var parentPath = trimmed[..(fileStart - 1)].TrimEnd('/', '\\');
        var parentStart = parentPath.LastIndexOfAny(['/', '\\']) + 1;
        var parentFolder = parentPath[parentStart..];

        // A drive root such as "C:" is not a folder name.
        if (parentFolder.EndsWith(':'))
        {
            parentFolder = string.Empty;
        }

        return (fileName, parentFolder);
    }

    private static bool TextIndicatesHdr10Plus(string? value)
    {
        return !string.IsNullOrEmpty(value)
            && (value.Contains("HDR10+", StringComparison.OrdinalIgnoreCase) || Hdr10PlusTokenRegex().IsMatch(value));
    }
}
