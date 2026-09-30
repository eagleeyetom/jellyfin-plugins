# Jellyfin Plugins

[![CI](https://github.com/eagleeyetom/jellyfin-plugins/actions/workflows/ci.yml/badge.svg)](https://github.com/eagleeyetom/jellyfin-plugins/actions/workflows/ci.yml)
[![Jellyfin](https://img.shields.io/badge/Jellyfin-%E2%89%A512.0.0-00A4DC?logo=jellyfin&logoColor=white)](https://jellyfin.org/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![GitHub Release](https://img.shields.io/github/v/release/eagleeyetom/jellyfin-plugins?logo=github&label=release)](https://github.com/eagleeyetom/jellyfin-plugins/releases)

A personal fork of [Atilil/jellyfin-plugins](https://github.com/Atilil/jellyfin-plugins), maintained for my own media server. This fork contains a small set of personal maintenance changes. It is not an official Jellyfin project and is not affiliated with or endorsed by Jellyfin or the original project.

## Installation

1. Open Jellyfin and go to **Administration → Dashboard → Plugins → Repositories**
2. Click **Add** and enter:
    - **Name:** `Personal Jellyfin Plugins`
    - **URL:** `https://raw.githubusercontent.com/eagleeyetom/jellyfin-plugins/refs/heads/main/manifest.json`
3. Click **Save**
4. Go to **Catalog** tab and install the plugins you want
5. Restart Jellyfin

## Available Plugins

### JellyTag

[![JellyTag Release](https://img.shields.io/github/v/release/eagleeyetom/jellyfin-plugins?filter=jellytag-*&label=release&color=00A4DC)](https://github.com/eagleeyetom/jellyfin-plugins/releases)

<p align="center">
    <img src="Jellytag/Jellyfin.Plugin.JellyTag/JellyTag.png" />
</p>

Adds quality badges for resolution, HDR, video codec, audio, languages, and subtitles to media posters and thumbnails. Badges are applied server-side via HTTP middleware, visible on all Jellyfin clients without client-side configuration.

**Features:**
- Automatic quality detection from video metadata
- Samsung/Tizen TV detection with Dolby Vision filtering and HDR10+ preservation
- Configurable badge position, size, and margin per image type
- Support for posters and thumbnails
- Custom SVG, PNG, and JPEG badges
- File-based image caching for performance
- Works on all clients (web, mobile, TV, Kodi)

[More details](Jellytag/README.md)

---

### Metadata Notifier

[![Metadata Notifier Release](https://img.shields.io/github/v/release/eagleeyetom/jellyfin-plugins?filter=metadatanotifier-*&label=release&color=00A4DC)](https://github.com/eagleeyetom/jellyfin-plugins/releases)

<p align="center">
    <img src="MetadataNotifier/Jellyfin.Plugin.MetadataNotifier/MetadataNotifier.png" width="200" />
</p>

Sends on-screen toast notifications when playback starts on any client, displaying HDR metadata systems (HDR10+, Dolby Vision, HDR10, HLG, SDR), audio codecs (TrueHD Atmos, DTS-HD MA), and transcoding status.

**Features:**
- Multi-client support (Samsung TVs, web, mobile, etc.)
- HDR, audio codec, and channel detection
- Transcoding status reporting
- Fully configurable display options and duration

[More details](MetadataNotifier/README.md)

---

### Requirements

- Jellyfin 12.0.0 or higher
- .NET 10 SDK (for building)

## License

MIT License - see [LICENSE](LICENSE) file.

## Repository

- **Forked from:** [Atilil/jellyfin-plugins](https://github.com/Atilil/jellyfin-plugins)
- **Fork maintenance:** eagleeyetom

---

## Disclaimer

This project was developed with the assistance of AI (Claude by Anthropic). The code has been reviewed, tested, and validated before publication.
