# Positioned Subtitles

<p align="center">
    <img src="Jellyfin.Plugin.PositionedSubtitles/PositionedSubtitles.png" width="200" />
</p>

Per-user subtitle styles and working SRT positioning for the Jellyfin web client and the TV apps built on it (Tizen, webOS). Nothing is written to the media folders: subtitles are styled while they are being served.

## Why

- The web player renders SRT as plain text tracks and strips `{\anN}` tags. Lines that the subtitle author moved to the top of the screen, for example so they do not cover on-screen text, still show at the bottom. Kodi honours these tags.
- ASS tracks are rendered by libass with the style from the file, usually white. The client's subtitle appearance settings do not apply to them.
- The client's appearance settings are stored only in the device's `localStorage` and are never sent to the server, so the server cannot reuse them.

## What it does

For every user with an enabled style (Dashboard → Plugins → Positioned Subtitles):

| Track | What the client gets |
| --- | --- |
| SRT with `{\anN}` in a selected language | Announced as ASS and converted on the fly with the user's style; positioning works |
| SRT without positioning tags | Unchanged, so the client's own appearance settings still apply |
| ASS/SSA in a selected language | White styles recoloured to the user's text colour; fonts, sizes, positions and coloured signs are kept |
| Anything else, or users without a style | Jellyfin's normal response |

Only the clients listed in the settings get these changes (default: `Jellyfin Web`, `Jellyfin for Tizen`, `Jellyfin for webOS`). Kodi and Android TV are left alone.

### How it works

- An HTTP middleware rewrites `Items/{id}/PlaybackInfo`. For the requesting user, positioned SRT tracks get codec `ass` and a `Stream.ass` URL. The subtitle URLs of the selected languages also get a `PsUser=<user id>` tag, because the libass worker downloads them without auth headers.
- `Videos/.../Subtitles/{index}/Stream.ass` is served through Jellyfin's own `Stream.srt` response, so authorisation and stream-index handling stay Jellyfin's. The SRT is converted with Jellyfin's ffmpeg and the user's style. ASS responses are recoloured. If anything fails, the client gets Jellyfin's normal response.
- The user comes from the request token; the `PsUser` tag is only a fallback. Changing the tag can only select another user's subtitle style; it gives no access to anything else.

### Subtitle cache repair

Jellyfin caches extracted subtitles as `data/subtitles/xx/<id>/<stream index>.srt` and checks the cache only against the video's size and modification time. Stream indices are ordered [external subtitles][external audio][embedded streams]. Adding or removing an external subtitle file (downloaded or added by hand) therefore shifts every embedded index, and the cache then serves the wrong language.

The scheduled task **Positioned subtitles maintenance** (Library, daily at 05:00):

- records the offset in `.positioned-subs.offset` in each cache folder and renames the cache files when the offset changes;
- extracts subtitles of the selected languages that are missing from the cache, because positioning is detected from the cache;
- removes `.ass` files written by v1, which carry the plugin's marker. Files without the marker are never touched.

Leaving `Write .ass files next to videos` on keeps the v1 behaviour instead.

## Settings

- **Languages:** ISO 639-2 or 639-1 codes, for example `pol` or `pl, en`.
- **Only SRT tracks with positioning tags:** when unchecked, every SRT track in the selected languages gets the user's style.
- **User styles:** for each user, an enable switch, font, size, colours, outline, shadow, bold, optional background box, vertical margin, and recolouring of ASS tracks. A live preview shows the result; libass on the TV can differ slightly.

## Build and install

```bash
dotnet test Jellyfin.Plugin.PositionedSubtitles.Tests
./build.sh                # output/: dll, meta.json, zip
./build.sh install        # copy into the local "jellyfin" container, then: docker restart jellyfin
```

The plugin is not in `manifest.json` yet; install it from the build output.

The test that pipes SRT through ffmpeg runs only when `POSUBS_FFMPEG` points to an ffmpeg binary or wrapper.

## Limitations

- It depends on the `PlaybackInfo` JSON shape and the subtitle URL format of Jellyfin 12; check it after major upgrades.
- Burned-in subtitles (transcoding) and clients outside the configured list are not styled.
