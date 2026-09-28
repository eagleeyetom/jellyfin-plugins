# Metadata Notifier Plugin for Jellyfin

<p align="center">
    <img src="https://github.com/eagleeyetom/jellyfin-plugins/raw/test/MetadataNotifier/Jellyfin.Plugin.MetadataNotifier/MetadataNotifier.png" alt="Metadata Notifier Logo" width="200" />
</p>

Sends on-screen toast notifications when playback starts on any Jellyfin client, displaying HDR metadata systems (HDR10+, Dolby Vision, HDR10, HLG, and SDR), audio codecs (TrueHD Atmos, DTS-HD MA, etc.), and transcoding status.

## Features

- **Multi-Client Support:** Works across Samsung Tizen TVs, web browsers, mobile apps, and other Jellyfin clients.
- **HDR & Metadata Detection:** Identifies HDR10+, Dolby Vision, HDR10, HLG, and SDR with multi-layer detection, with optional tone-mapped SDR detection when Jellyfin reports an unsupported HDR range.
- **Audio Codec & Channels:** Displays active audio formats including TrueHD Atmos, DTS-HD MA, DTS:X, AC3, E-AC3, AAC, and channel layouts (5.1, 7.1, etc.).
- **Live Audio Track Change Notification:** Shows a toast notification whenever the viewer switches the audio track mid-playback.
- **Transcoding Status & Reasons:** Reports whether playback is Direct Play, Direct Stream, or Transcoding, including exact reasons (e.g. incompatible audio codec or subtitles).
- **Audio Conversion Details:** Displays real-time audio conversion (e.g. `TrueHD 7.1 ➔ AC3 5.1 (Transcoded)`).
- **Custom Notification Template:** Customize the order and layout of notification items with flexible template variables.
- **Exclusion Lists:** Easily exclude specific media libraries or users from toast notifications.
- **Fully Configurable:** Customize notification duration, target clients, and displayed information categories.


