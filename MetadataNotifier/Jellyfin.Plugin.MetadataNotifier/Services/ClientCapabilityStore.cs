using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Jellyfin.Plugin.MetadataNotifier.Services;

/// <summary>
/// Remembers the latest video range capabilities each device sent with a PlaybackInfo request,
/// keyed by device ID (the same ID the playback session reports).
/// </summary>
public class ClientCapabilityStore
{
    internal const int MaxDevices = 1000;

    private readonly ConcurrentDictionary<string, Entry> _byDevice = new(StringComparer.OrdinalIgnoreCase);

    internal int Count => _byDevice.Count;

    internal void Set(string? deviceId, ClientVideoCapabilities capabilities)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }

        _byDevice[deviceId] = new Entry(capabilities, DateTime.UtcNow);

        if (_byDevice.Count > MaxDevices)
        {
            foreach (var stale in _byDevice.OrderBy(e => e.Value.UpdatedUtc).Take(_byDevice.Count - MaxDevices).ToList())
            {
                _byDevice.TryRemove(stale.Key, out _);
            }
        }
    }

    internal ClientVideoCapabilities? Get(string? deviceId)
    {
        return !string.IsNullOrWhiteSpace(deviceId) && _byDevice.TryGetValue(deviceId, out var entry)
            ? entry.Capabilities
            : null;
    }

    private sealed record Entry(ClientVideoCapabilities Capabilities, DateTime UpdatedUtc);
}
