# Metadata Notifier Plugin for Jellyfin

[![Release](https://img.shields.io/github/v/release/eagleeyetom/jellyfin-plugins?filter=metadatanotifier-*&label=release&color=00A4DC)](https://github.com/eagleeyetom/jellyfin-plugins/releases)
[![Jellyfin](https://img.shields.io/badge/Jellyfin-%E2%89%A512.0.0-00A4DC?logo=jellyfin&logoColor=white)](https://jellyfin.org/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](../LICENSE)

<p align="center">
    <img src="https://raw.githubusercontent.com/eagleeyetom/jellyfin-plugins/main/MetadataNotifier/Jellyfin.Plugin.MetadataNotifier/MetadataNotifier.png" alt="Metadata Notifier Logo" width="200" />
</p>

Sends on-screen toast notifications when playback starts on any Jellyfin client, displaying HDR metadata systems (HDR10+, Dolby Vision, HDR10, HLG, and SDR), audio codecs (TrueHD Atmos, DTS-HD MA, etc.), and transcoding status.

## Features

- **Multi-Client Support:** Works across Samsung Tizen TVs, web browsers, mobile apps, and other Jellyfin clients.
- **HDR & Metadata Detection:** Identifies HDR10+, Dolby Vision, HDR10, HLG, and SDR with multi-layer detection, with optional tone-mapped SDR detection when Jellyfin reports an unsupported HDR range.
- **Reports what the client can show:** When a client can't decode Dolby Vision or HDR10+, the toast reports the layer it actually plays (e.g. HDR10). This is read from the device profile the client sends when playback starts, not guessed from its name. Clients that send no profile fall back to name matching (Samsung, LG, Sony, Android phones, ...).
- **Audio Codec & Channels:** Displays active audio formats including TrueHD Atmos, DTS-HD MA, DTS:X, AC3, E-AC3, AAC, and channel layouts (5.1, 7.1, etc.).
- **Live Audio Track Change Notification:** Shows a toast notification whenever the viewer switches the audio track mid-playback.
- **Transcoding Status & Reasons:** Reports whether playback is Direct Play, Direct Stream, or Transcoding, including exact reasons (e.g. incompatible audio codec or subtitles).
- **Audio Conversion Details:** Displays real-time audio conversion (e.g. `TrueHD 7.1 ➔ AC3 5.1 (Transcoded)`).
- **Custom Notification Template:** Customize the order and layout of notification items with flexible template variables.
- **Exclusion Lists:** Easily exclude specific media libraries or users from toast notifications.
- **Optional Browser Fullscreen Toast Fix:** In web browsers, HTML5 fullscreen mode can hide standard toast notifications behind the video element. An optional server setting dynamically patches `index.html` on HTTP requests to keep toasts visible in fullscreen on web browsers. (Disabled by default; not needed for Samsung Tizen or native TV clients which display toasts natively).
- **Config Export/Import & Reset:** Easily backup and restore your configuration as JSON, or reset all settings to defaults with a single click.
- **Fully Configurable:** Customize notification duration, target clients, and displayed information categories.

## Client Support

| Client | Toast shown | Notes |
| --- | --- | --- |
| Jellyfin Web (browsers) | Yes | Enable the Fullscreen Toast Fix if toasts disappear in fullscreen. |
| Samsung (Tizen) | Yes | |
| Android TV / Google TV / Fire TV (official app) | Yes | |
| Wholphin | Yes | |
| Kodi (Jellyfin add-ons) | Yes | Uses its own display timing. |
| Jellyfin for Android (phones/tablets) | Only with the web player | The default "Integrated player" (ExoPlayer) doesn't handle server messages; the toast is drawn behind it. In the app: Settings → Video player type → Web player. |
| Other clients (Swiftfin, Findroid, Infuse, Roku, webOS, ...) | Not verified | Depends on whether the client handles the DisplayMessage command. |

## Known Limitations

- **Client-side HDR tone-mapping is only detected in browsers.** "Tone-mapped SDR detection" fires when Jellyfin tone-maps server-side, and in Jellyfin Web on a desktop or mobile browser whose display is not in HDR mode (e.g. Windows with "Use HDR" off): the plugin adds a small script to the web client that reports the display's range, so reload the web client after enabling the option. Apps and TVs that tone-map on their own are still reported with the source format, because that is genuinely what Jellyfin sent.
- **The device profile describes the player, not the screen.** A Fire TV or Shield that decodes Dolby Vision reports it even when its TV can't show it; use "Always report the fallback on Fire TV" for that case. Web clients claim HDR10+ wherever they support HDR10, so HDR10+ on LG/Sony/Roku/etc. is still corrected by name, and Chrome, Edge, Firefox and Opera (which play only the HDR10 base layer) report HDR10.
- **Toast duration and delivery are client-dependent.** The configured notification duration is sent as a hint (`TimeoutMs`); some clients (e.g. Jellyfin Web, Kodi) use their own fixed display timing and may ignore it. Some clients may not render the message at all.


