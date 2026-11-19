# Jellyfin Positioned Subtitles (local plugin)

The Jellyfin web/Tizen client ignores `{\anN}` positioning in SRT. Kodi honours it.
**v2: live styling (default).** A middleware announces SRT tracks (selected languages, with `{\anN}`) to web/Tizen clients as ASS and serves them converted with the configured style; embedded ASS tracks are served recoloured. No files are written. The scheduled task **Positioned subtitles maintenance** repairs the cache, extracts missing subtitles and removes .ass files written by v1. Old v1 behaviour (`WriteSidecarFiles`):

1. reads embedded SRT tracks of the selected languages from the subtitle cache
   (`/config/data/subtitles/xx/<item id>/<stream index>.srt`, filled by Subtitle Extract);
2. for tracks with `{\anN}`, writes `<video>.<lang>.ass` next to the video with the configured style
   (default: yellow, 64 px at 1080p, outline 3). It never overwrites files without its marker;
3. repairs the cache naming: Jellyfin numbers streams as [external subs][external audio][embedded],
   so every added or removed external file shifts embedded indices, and the cache, validated only
   by the video's size and mtime, serves the wrong language. The plugin records the offset in
   `.positioned-subs.offset` in each cache folder and renames the files when it changes.
   It shifts the cache *before* refreshing the item so Subtitle Extract does not re-read the video.

Build: `./build.sh` (pulls reference DLLs from the `jellyfin` container); install: `./build.sh install`
and `docker restart jellyfin`. **Rebuild after every Jellyfin upgrade** that changes the ABI.
