// Shared between JellyTag and Metadata Notifier. This file is compiled into each plugin
// (see the <Compile Include="..\..\Shared\..." Link="..."> items in both .csproj files) so that
// each plugin still ships as a single self-contained DLL. Keep everything here internal and free
// of plugin-specific types, and change it in one place only.
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Shared.MediaDetection;

/// <summary>
/// Pure helpers that classify a single client identifier (client name, device name, user agent,
/// ...) as coming from a Samsung TV or an Amazon Fire TV device.
/// </summary>
internal static partial class ClientDeviceDetection
{
    // Amazon model codes are upper-case (AFTMM, AFTKA, AFTKMST, ...). Matched case-sensitively:
    // JellyTag used to match case-insensitively, which also caught words such as "Aftermath".
    [GeneratedRegex(@"\bAFT[A-Z0-9]{1,8}\b", RegexOptions.CultureInvariant)]
    private static partial Regex AftModelRegex();

    // Whole words only: "Firefox" has no word boundary after "Fire", so it never matches, which
    // replaces JellyTag's former "contains Firefox" exclusion (that one also rejected values such
    // as "Fire TV (Firefox)"), and "Amazon" no longer matches inside longer words.
    [GeneratedRegex(@"\b(?:Fire|Amazon)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FireTvWordRegex();

    /// <summary>
    /// Returns true for Samsung TV identifiers (Tizen, "Samsung Smart TV", ...), but not for Samsung
    /// phones, tablets or the Samsung Internet browser running on them.
    /// </summary>
    internal static bool ContainsSamsungTvIndicator(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value.Contains("Tizen", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!value.Contains("Samsung", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Galaxy devices ("Galaxy S23", model codes "SM-S918B") and Samsung Internet outside Tizen
        // (phones, tablets, DeX desktop mode) are not TVs. The "SamsungBrowser" exclusion is new:
        // neither plugin had it, so a DeX user agent used to count as a Samsung TV.
        return !value.Contains("Galaxy", StringComparison.OrdinalIgnoreCase)
            && !value.Contains("SM-", StringComparison.OrdinalIgnoreCase)
            && !value.Contains("SamsungBrowser", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true for Fire TV identifiers: explicit names ("Fire TV", "FireStick", "FireOS", ...),
    /// Amazon model codes (AFTxxx) and the whole words "Fire" or "Amazon". Firefox is never a match.
    /// </summary>
    internal static bool ContainsFireTvIndicator(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        // JellyTag also accepted any "Silk/" (Amazon Silk browser) user agent. That is dropped: on a
        // Fire TV the Silk user agent also carries the AFTxxx model code, and on its own it mostly
        // identifies Fire tablets, which are not Fire TV devices.
        return value.Contains("Fire TV", StringComparison.OrdinalIgnoreCase)
            || value.Contains("FireTV", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Fire-TV", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Fire_TV", StringComparison.OrdinalIgnoreCase)
            || value.Contains("FireStick", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Fire Stick", StringComparison.OrdinalIgnoreCase)
            || value.Contains("FireOS", StringComparison.OrdinalIgnoreCase)
            || AftModelRegex().IsMatch(value)
            || FireTvWordRegex().IsMatch(value);
    }
}
