#!/bin/bash
# Build script for JellyTag Jellyfin plugin
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PLUGIN_DIR="$SCRIPT_DIR/Jellyfin.Plugin.JellyTag"
OUTPUT_DIR="$SCRIPT_DIR/output"

echo "=== Building JellyTag Plugin ==="

# Clean output directory
rm -rf "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"

# Build and publish plugin
echo "Compiling plugin..."
dotnet publish "$PLUGIN_DIR/Jellyfin.Plugin.JellyTag.csproj" -c Release -o "$PLUGIN_DIR/publish_out"

# Copy published files
echo "Copying files..."
cp "$PLUGIN_DIR/publish_out"/*.dll "$OUTPUT_DIR/"

VERSION="$(dotnet msbuild "$PLUGIN_DIR/Jellyfin.Plugin.JellyTag.csproj" -getProperty:Version | tr -d '\r\n')"
if [ -z "$VERSION" ]; then
    VERSION="2.4.0.0"
fi

# Create meta.json for the plugin
cat > "$OUTPUT_DIR/meta.json" << EOF
{
    "guid": "f4a2e8c1-9b3d-4f7a-b6c5-2d8e1a3f9b04",
    "name": "JellyTag",
    "overview": "Adds quality badges (4K, 1080p, etc.) to media posters and thumbnails.",
    "description": "JellyTag automatically adds quality resolution badges to your media posters and thumbnails. Badges are visible on all clients including web, mobile, TV, and Kodi.",
    "owner": "eagleeyetom",
    "category": "General",
    "version": "$VERSION",
    "targetAbi": "10.11.0.0"
}
EOF

# Create ZIP archive
echo "Creating ZIP archive..."
cd "$OUTPUT_DIR"
zip -r "jellytag-$VERSION.zip" *.dll meta.json
cd "$SCRIPT_DIR"

echo ""
echo "=== Build complete ==="
echo "Output files in: $OUTPUT_DIR/"
echo ""
echo "To install:"
echo "1. Copy the DLLs to your Jellyfin plugins folder:"
echo "   mkdir -p /path/to/jellyfin/plugins/JellyTag"
echo "   cp $OUTPUT_DIR/*.dll /path/to/jellyfin/plugins/JellyTag/"
echo "2. Restart Jellyfin"
