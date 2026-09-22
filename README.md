# Jellyfin Plugins

A personal fork of Jellyfin plugins maintained for my own media server. This repository is not an official Jellyfin project and is not affiliated with or endorsed by Jellyfin or the original plugin authors.

## Installation

1. Open Jellyfin and go to **Administration → Dashboard → Plugins → Repositories**
2. Click **Add** and enter:
    - **Name:** `Personal Jellyfin Plugins`
    - **URL:** `https://raw.githubusercontent.com/eagleeyetom/jellyfin-plugins/refs/heads/samsung-client-filter-test/manifest.json`
3. Click **Save**
4. Go to **Catalog** tab and install the plugins you want
5. Restart Jellyfin

## Available Plugins

### WatchSync

<p align="center">
    <img src="WatchSync/Jellyfin.Plugin.WatchSync/WatchSync.png" />
</p>

Automatically synchronizes watch history between libraries of different qualities (4K/HD). When a movie is watched in 4K, the HD version is also marked as watched (and vice versa).

**Features:**
- Automatic sync on playback stop
- Smart matching via IMDB, TMDB, TVDB
- Support for movies and TV series
- Configurable completion threshold
- Library and user exclusion
- Full sync scheduled task

[More details](WatchSync/README.md)

---

### JellyTag

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

### Requirements

- Jellyfin 10.11.0 or higher
- .NET 9 SDK (for building)

## License

MIT License - see [LICENSE](LICENSE) file.

## Author

**eagleeyetom**

---

## Disclaimer

This project was developed with the assistance of AI (Claude by Anthropic). The code has been reviewed, tested, and validated before publication.
