using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Jellyfin.Plugin.MetadataNotifier.Services;

/// <summary>
/// Remembers the latest video range capabilities each device sent with a PlaybackInfo request,
/// keyed by device ID (the same ID the playback session reports), and whether the browser's
/// display was in HDR mode at that time.
/// </summary>
public class ClientCapabilityStore
{
    internal const int MaxDevices = 1000;

    private readonly ConcurrentDictionary<string, Entry<ClientVideoCapabilities>> _byDevice = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Entry<bool>> _displayHdrByDevice = new(StringComparer.OrdinalIgnoreCase);

    internal int Count => _byDevice.Count;

    internal void Set(string? deviceId, ClientVideoCapabilities capabilities) => Set(_byDevice, deviceId, capabilities);

    internal ClientVideoCapabilities? Get(string? deviceId)
    {
        return !string.IsNullOrWhiteSpace(deviceId) && _byDevice.TryGetValue(deviceId, out var entry)
            ? entry.Value
            : null;
    }

    internal void SetDisplayHdr(string? deviceId, bool isHdr) => Set(_displayHdrByDevice, deviceId, isHdr);

    /// <summary>
    /// True or false when the device's browser reported its display range, otherwise null.
    /// </summary>
    internal bool? GetDisplayHdr(string? deviceId)
    {
        return !string.IsNullOrWhiteSpace(deviceId) && _displayHdrByDevice.TryGetValue(deviceId, out var entry)
            ? entry.Value
            : null;
    }

    private static void Set<T>(ConcurrentDictionary<string, Entry<T>> byDevice, string? deviceId, T value)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }

        byDevice[deviceId] = new Entry<T>(value, DateTime.UtcNow);

        if (byDevice.Count > MaxDevices)
        {
            foreach (var stale in byDevice.OrderBy(e => e.Value.UpdatedUtc).Take(byDevice.Count - MaxDevices).ToList())
            {
                byDevice.TryRemove(stale.Key, out _);
            }
        }
    }

    private sealed record Entry<T>(T Value, DateTime UpdatedUtc);
}
