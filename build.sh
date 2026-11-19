#!/usr/bin/env bash
# Builds the plugin against the DLLs of the running Jellyfin container and prepares dist/.
# Usage: ./build.sh            -> build into dist/
#        ./build.sh install    -> also copy into the container's plugin folder (restart Jellyfin afterwards)
set -euo pipefail
cd "$(dirname "$0")"
CONTAINER=${CONTAINER:-jellyfin}
VERSION=1.0.0.0
NAME="Positioned Subtitles"

mkdir -p ref
for d in MediaBrowser.Common MediaBrowser.Controller MediaBrowser.Model Jellyfin.Data Jellyfin.Extensions Jellyfin.Database.Implementations; do
  docker cp "$CONTAINER:/jellyfin/$d.dll" ref/ >/dev/null
done
SERVER=$(docker exec "$CONTAINER" sh -c 'curl -s localhost:8096/System/Info/Public' | sed -n 's/.*"Version":"\([0-9.]*\)".*/\1/p')
ABI="${SERVER}.0"

dotnet build src -c Release -nologo -v quiet -o build/out
rm -rf dist && mkdir -p dist
cp build/out/Jellyfin.Plugin.PositionedSubtitles.dll dist/
cat > dist/meta.json <<EOF
{
  "category": "Subtitles",
  "changelog": "Built against Jellyfin $SERVER",
  "description": "Converts embedded SRT subtitles with {\\\\anN} positioning to styled external ASS files and repairs shifted subtitle cache indices.",
  "guid": "2f8a634e-fa86-4c06-bfa1-a50c1c53908a",
  "name": "$NAME",
  "overview": "Positioned SRT to ASS",
  "owner": "local",
  "targetAbi": "$ABI",
  "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "version": "$VERSION",
  "status": "Active",
  "autoUpdate": false,
  "assemblies": []
}
EOF
echo "Built dist/ for Jellyfin $SERVER (targetAbi $ABI)"

if [ "${1:-}" = install ]; then
  DEST="/config/plugins/${NAME}_${VERSION}"
  docker exec "$CONTAINER" mkdir -p "$DEST"
  docker cp dist/. "$CONTAINER:$DEST/"
  echo "Installed to $DEST - restart the container: docker restart $CONTAINER"
fi
