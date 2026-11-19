using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Globalization;

namespace Jellyfin.Plugin.PositionedSubtitles.Services;

public static class LanguageMatcher
{
    public static HashSet<string> Parse(string? value)
    {
        return (value ?? string.Empty)
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static bool Matches(ILocalizationManager localization, string? language, IReadOnlyCollection<string> wanted)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return false;
        }

        if (wanted.Contains(language))
        {
            return true;
        }

        var culture = localization.FindLanguageInfo(language);
        if (culture is null)
        {
            return false;
        }

        return wanted.Contains(culture.TwoLetterISOLanguageName)
               || culture.ThreeLetterISOLanguageNames.Any(wanted.Contains);
    }
}
