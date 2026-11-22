#!/bin/bash
# Build script for BadgeFin Jellyfin plugin
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PLUGIN_DIR="$SCRIPT_DIR/Jellyfin.Plugin.BadgeFin"
OUTPUT_DIR="$SCRIPT_DIR/output"

echo "=== Building BadgeFin Plugin ==="

# Clean output directory
rm -rf "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"

# Build and publish plugin
echo "Compiling plugin..."
dotnet publish "$PLUGIN_DIR/Jellyfin.Plugin.BadgeFin.csproj" -c Release -o "$PLUGIN_DIR/publish_out"

# Copy published files
echo "Copying files..."
cp "$PLUGIN_DIR/publish_out"/*.dll "$OUTPUT_DIR/"

VERSION="$(dotnet msbuild "$PLUGIN_DIR/Jellyfin.Plugin.BadgeFin.csproj" -getProperty:Version | tr -d '\r\n')"
if [ -z "$VERSION" ]; then
    VERSION="2.4.0.0"
fi

# Create meta.json for the plugin
cat > "$OUTPUT_DIR/meta.json" << EOF
{
    "guid": "7354bd14-00d2-410e-9086-4f2b0e1a300d",
    "name": "BadgeFin",
    "overview": "Adds quality badges (4K, 1080p, etc.) to media posters and thumbnails.",
    "description": "BadgeFin automatically adds quality resolution badges to your media posters and thumbnails. Badges are visible on all clients including web, mobile, TV, and Kodi.",
    "owner": "eagleeyetom",
    "category": "General",
    "version": "$VERSION",
    "targetAbi": "12.0.0.0"
}
EOF

# Create ZIP archive
echo "Creating ZIP archive..."
cd "$OUTPUT_DIR"
zip -r "badgefin-$VERSION.zip" *.dll meta.json
cd "$SCRIPT_DIR"

echo ""
echo "=== Build complete ==="
echo "Output files in: $OUTPUT_DIR/"
echo ""
echo "To install:"
echo "1. Copy the DLLs to your Jellyfin plugins folder:"
echo "   mkdir -p /path/to/jellyfin/plugins/BadgeFin"
echo "   cp $OUTPUT_DIR/*.dll /path/to/jellyfin/plugins/BadgeFin/"
echo "2. Restart Jellyfin"
