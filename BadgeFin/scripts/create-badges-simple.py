#!/usr/bin/env python3
"""
Generate minimal placeholder PNG badges without external dependencies.
These are simple 1x1 colored pixels that will be scaled.
Replace with proper badges for production use.
"""

import os
import struct
import zlib

def create_minimal_png(width, height, r, g, b, a=255):
    """Create a minimal solid color PNG image."""

    def png_chunk(chunk_type, data):
        chunk_len = struct.pack('>I', len(data))
        chunk_crc = struct.pack('>I', zlib.crc32(chunk_type + data) & 0xffffffff)
        return chunk_len + chunk_type + data + chunk_crc

    # PNG signature
    signature = b'\x89PNG\r\n\x1a\n'

    # IHDR chunk
    ihdr_data = struct.pack('>IIBBBBB', width, height, 8, 6, 0, 0, 0)  # 8-bit RGBA
    ihdr = png_chunk(b'IHDR', ihdr_data)

    # IDAT chunk (image data)
    raw_data = b''
    for y in range(height):
        raw_data += b'\x00'  # Filter type: None
        for x in range(width):
            raw_data += bytes([r, g, b, a])

    compressed = zlib.compress(raw_data, 9)
    idat = png_chunk(b'IDAT', compressed)

    # IEND chunk
    iend = png_chunk(b'IEND', b'')

    return signature + ihdr + idat + iend

def main():
    os.makedirs("Jellyfin.Plugin.BadgeFin/Assets", exist_ok=True)

    # Badge configurations: (filename, R, G, B)
    # These are solid color placeholders - replace with proper badges
    badges = [
        ("badge-4k.png", 255, 193, 7),      # Gold/Amber for 4K
        ("badge-1080p.png", 224, 224, 224), # Light gray for 1080p
        ("badge-720p.png", 158, 158, 158),  # Medium gray for 720p
        ("badge-sd.png", 97, 97, 97),       # Dark gray for SD
    ]

    # Create small placeholder badges (40x20 pixels)
    for filename, r, g, b in badges:
        png_data = create_minimal_png(40, 20, r, g, b)
        path = os.path.join("Jellyfin.Plugin.BadgeFin/Assets", filename)
        with open(path, 'wb') as f:
            f.write(png_data)
        print(f"Created placeholder: {path}")

    print("\nPlaceholder badges created!")
    print("These are solid color rectangles - replace with proper badge images.")
    print("Recommended badge size: 80-100px width, with text and rounded corners.")

if __name__ == "__main__":
    main()
