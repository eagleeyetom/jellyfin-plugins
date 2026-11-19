#!/usr/bin/env bash
# Builds Positioned Subtitles and packages it like a catalogue release.
# Usage: ./build.sh            -> output/ (dll, meta.json, zip)
#        ./build.sh install    -> also copy it into a local Jellyfin container (CONTAINER=jellyfin) and print the restart command
set -euo pipefail
cd "$(dirname "$0")"

PROJECT=Jellyfin.Plugin.PositionedSubtitles/Jellyfin.Plugin.PositionedSubtitles.csproj
NAME="Positioned Subtitles"
CONTAINER=${CONTAINER:-jellyfin}
VERSION=$(dotnet msbuild "$PROJECT" -getProperty:Version | tr -d '[:space:]')
ABI=$(sed -n 's/^targetAbi: *//p' build.yaml)

rm -rf output Jellyfin.Plugin.PositionedSubtitles/publish_out
dotnet publish "$PROJECT" -c Release -nologo -v quiet -o Jellyfin.Plugin.PositionedSubtitles/publish_out
mkdir -p output
cp Jellyfin.Plugin.PositionedSubtitles/publish_out/Jellyfin.Plugin.PositionedSubtitles.dll output/
cp Jellyfin.Plugin.PositionedSubtitles/PositionedSubtitles.png output/
rm -rf Jellyfin.Plugin.PositionedSubtitles/publish_out

cat > output/meta.json <<EOF
{
  "category": "Subtitles",
  "description": "Forces a per-user subtitle style on the fly: positioned SRT tracks are served as styled ASS and white ASS tracks are recoloured.",
  "guid": "2f8a634e-fa86-4c06-bfa1-a50c1c53908a",
  "name": "$NAME",
  "overview": "Per-user subtitle styles and SRT positioning for web and TV clients",
  "owner": "eagleeyetom",
  "targetAbi": "$ABI",
  "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "version": "$VERSION",
  "status": "Active",
  "autoUpdate": false,
  "assemblies": []
}
EOF
(cd output && zip -q "positionedsubtitles-$VERSION.zip" Jellyfin.Plugin.PositionedSubtitles.dll meta.json PositionedSubtitles.png)
echo "Built output/ v$VERSION (targetAbi $ABI)"

if [ "${1:-}" = install ]; then
  docker exec "$CONTAINER" sh -c "rm -rf \"/config/plugins/${NAME}_\"*"
  DEST="/config/plugins/${NAME}_${VERSION}"
  docker exec "$CONTAINER" mkdir -p "$DEST"
  docker cp output/Jellyfin.Plugin.PositionedSubtitles.dll "$CONTAINER:$DEST/"
  docker cp output/PositionedSubtitles.png "$CONTAINER:$DEST/"
  # A manually installed plugin shows its icon only when meta.json points to it.
  sed "s#\"assemblies\": \[\]#\"imagePath\": \"$DEST/PositionedSubtitles.png\",\n  \"assemblies\": []#" output/meta.json > output/meta.install.json
  docker cp output/meta.install.json "$CONTAINER:$DEST/meta.json"
  rm -f output/meta.install.json
  echo "Installed to $DEST - restart Jellyfin: docker restart $CONTAINER"
fi
