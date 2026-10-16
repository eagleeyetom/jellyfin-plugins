using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Dlna;

namespace Jellyfin.Plugin.MetadataNotifier.Services;

/// <summary>
/// Whether a client's device profile allows a video range type.
/// </summary>
internal enum RangeSupport
{
    /// <summary>
    /// The profile says nothing about range types for this codec (or there is no profile).
    /// </summary>
    Unknown,

    /// <summary>
    /// The profile declares range types for this codec and allows this one.
    /// </summary>
    Supported,

    /// <summary>
    /// The profile rejects this range type for this codec.
    /// </summary>
    Unsupported
}

/// <summary>
/// The <c>VideoRangeType</c> rules from a client's device profile. Clients send the profile with
/// every <c>POST /Items/{id}/PlaybackInfo</c>; the server uses it for that request only, so the
/// plugin keeps its own copy (see <see cref="ClientCapabilityStore"/>).
/// </summary>
/// <remarks>
/// Two encodings are in use. jellyfin-web lists allowed types (<c>EqualsAny "SDR|HDR10|..."</c>).
/// jellyfin-androidtv lists rejected types (<c>NotEquals</c> gated by an <c>ApplyConditions</c>
/// <c>EqualsAny</c> of the same list). Both are handled by evaluating the conditions.
/// Note that "allowed" means "can be played", not "is displayed as": web clients on Tizen, VIDAA
/// and webOS without DV allow <c>DOVIWithHDR10</c> because they play its HDR10 base layer, and
/// every HDR-capable browser allows <c>HDR10Plus</c>. Only plain <c>DOVI</c> (profile 5, no
/// fallback layer) is allowed exclusively by clients that really decode Dolby Vision.
/// </remarks>
internal sealed class ClientVideoCapabilities
{
    private readonly IReadOnlyList<RangeProfile> _profiles;

    private ClientVideoCapabilities(IReadOnlyList<RangeProfile> profiles)
    {
        _profiles = profiles;
    }

    /// <summary>
    /// Builds the capabilities from a typed device profile, e.g. <c>session.Capabilities.DeviceProfile</c>.
    /// Returns null when the profile has no range-type rules.
    /// </summary>
    public static ClientVideoCapabilities? FromDeviceProfile(DeviceProfile? profile)
    {
        if (profile?.CodecProfiles == null)
        {
            return null;
        }

        var profiles = new List<RangeProfile>();
        foreach (var codecProfile in profile.CodecProfiles)
        {
            if (codecProfile == null || codecProfile.Type != CodecType.Video)
            {
                continue;
            }

            var conditions = ToRangeConditions(codecProfile.Conditions);
            var applyConditions = ToRangeConditions(codecProfile.ApplyConditions);
            if (conditions.Count > 0 || applyConditions.Count > 0)
            {
                profiles.Add(new RangeProfile(SplitList(codecProfile.Codec), codecProfile.Container, applyConditions, conditions));
            }
        }

        return profiles.Count > 0 ? new ClientVideoCapabilities(profiles) : null;
    }

    /// <summary>
    /// Builds the capabilities from a PlaybackInfo request body (<c>{ "DeviceProfile": { ... } }</c>).
    /// Accepts enum values as names or numbers and property names in any case. Returns null when
    /// the body has no device profile or the profile has no range-type rules.
    /// </summary>
    public static ClientVideoCapabilities? FromPlaybackInfoBody(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object
            || !TryGetProperty(body, "DeviceProfile", out var deviceProfile)
            || !TryGetProperty(deviceProfile, "CodecProfiles", out var codecProfiles)
            || codecProfiles.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var profiles = new List<RangeProfile>();
        foreach (var codecProfile in codecProfiles.EnumerateArray())
        {
            if (codecProfile.ValueKind != JsonValueKind.Object
                || !TryGetProperty(codecProfile, "Type", out var type)
                || !TryReadEnum<CodecType>(type, out var codecType)
                || codecType != CodecType.Video)
            {
                continue;
            }

            var conditions = ReadRangeConditions(codecProfile, "Conditions");
            var applyConditions = ReadRangeConditions(codecProfile, "ApplyConditions");
            if (conditions.Count == 0 && applyConditions.Count == 0)
            {
                continue;
            }

            profiles.Add(new RangeProfile(
                SplitList(ReadString(codecProfile, "Codec")),
                ReadString(codecProfile, "Container"),
                applyConditions,
                conditions));
        }

        return profiles.Count > 0 ? new ClientVideoCapabilities(profiles) : null;
    }

    /// <summary>
    /// Evaluates whether <paramref name="rangeType"/> is allowed for <paramref name="codec"/> in
    /// <paramref name="container"/>. Profiles restricted to particular containers are skipped
    /// when the container is not known.
    /// </summary>
    public RangeSupport Supports(string? codec, VideoRangeType rangeType, string? container = null)
    {
        var rangeName = rangeType.ToString();
        var declared = false;

        foreach (var profile in _profiles)
        {
            if (!profile.MatchesCodec(codec) || !profile.MatchesContainer(container))
            {
                continue;
            }

            declared = true;
            if (profile.ApplyConditions.Any(c => !c.IsSatisfiedBy(rangeName)))
            {
                continue;
            }

            if (profile.Conditions.Any(c => !c.IsSatisfiedBy(rangeName)))
            {
                return RangeSupport.Unsupported;
            }
        }

        return declared ? RangeSupport.Supported : RangeSupport.Unknown;
    }

    private static List<RangeCondition> ToRangeConditions(IEnumerable<ProfileCondition>? conditions)
    {
        return conditions?
            .Where(c => c != null && c.Property == ProfileConditionValue.VideoRangeType)
            .Select(c => new RangeCondition(c.Condition, SplitValues(c.Value)))
            .ToList() ?? new List<RangeCondition>();
    }

    private static List<RangeCondition> ReadRangeConditions(JsonElement codecProfile, string name)
    {
        var result = new List<RangeCondition>();
        if (!TryGetProperty(codecProfile, name, out var conditions) || conditions.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var condition in conditions.EnumerateArray())
        {
            if (condition.ValueKind == JsonValueKind.Object
                && TryGetProperty(condition, "Property", out var property)
                && TryReadEnum<ProfileConditionValue>(property, out var propertyValue)
                && propertyValue == ProfileConditionValue.VideoRangeType
                && TryGetProperty(condition, "Condition", out var conditionType)
                && TryReadEnum<ProfileConditionType>(conditionType, out var type))
            {
                result.Add(new RangeCondition(type, SplitValues(ReadString(condition, "Value"))));
            }
        }

        return result;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? ReadString(JsonElement element, string name)
    {
        return TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool TryReadEnum<TEnum>(JsonElement value, out TEnum result)
        where TEnum : struct, Enum
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                result = (TEnum)Enum.ToObject(typeof(TEnum), number);
                return Enum.IsDefined(result);
            }

            return Enum.TryParse(text, ignoreCase: true, out result);
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numeric))
        {
            result = (TEnum)Enum.ToObject(typeof(TEnum), numeric);
            return Enum.IsDefined(result);
        }

        result = default;
        return false;
    }

    private static string[] SplitValues(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string[] SplitList(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string NormalizeCodec(string codec)
    {
        return codec.ToLowerInvariant() switch
        {
            "h265" or "hev1" or "hvc1" => "hevc",
            "avc" or "avc1" => "h264",
            _ => codec.ToLowerInvariant()
        };
    }

    private sealed record RangeCondition(ProfileConditionType Type, string[] Values)
    {
        public bool IsSatisfiedBy(string rangeType)
        {
            var listed = Values.Contains(rangeType, StringComparer.OrdinalIgnoreCase);
            return Type switch
            {
                ProfileConditionType.Equals or ProfileConditionType.EqualsAny => listed,
                ProfileConditionType.NotEquals => !listed,
                _ => true
            };
        }
    }

    private sealed record RangeProfile(
        string[] Codecs,
        string? Container,
        IReadOnlyList<RangeCondition> ApplyConditions,
        IReadOnlyList<RangeCondition> Conditions)
    {
        public bool MatchesCodec(string? codec)
        {
            if (Codecs.Length == 0)
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(codec))
            {
                return false;
            }

            var normalized = NormalizeCodec(codec);
            return Codecs.Any(c => string.Equals(NormalizeCodec(c), normalized, StringComparison.Ordinal));
        }

        /// <summary>
        /// Same rules as Jellyfin's container lists: empty = any, "a,b" = only these,
        /// "-a,b" = all except these. A media source container may itself be a list
        /// ("mov,mp4,m4a"), in which case any overlap counts.
        /// </summary>
        public bool MatchesContainer(string? container)
        {
            if (string.IsNullOrWhiteSpace(Container))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(container))
            {
                return false;
            }

            var exclude = Container.StartsWith('-');
            var listed = SplitList(exclude ? Container[1..] : Container);
            var actual = SplitList(container);
            var overlaps = actual.Any(a => listed.Contains(a, StringComparer.OrdinalIgnoreCase));
            return exclude ? !overlaps : overlaps;
        }
    }
}
