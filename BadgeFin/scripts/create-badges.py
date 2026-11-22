#!/usr/bin/env python3
"""
Generate quality badge images for BadgeFin plugin.
Requires: pip install Pillow
"""

from PIL import Image, ImageDraw, ImageFont
import os

def create_badge(text, bg_color, text_color, filename):
    """Create a badge image with rounded corners."""
    # Dimensions
    padding = 8
    font_size = 20

    # Try to use a nice font, fall back to default
    try:
        font = ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf", font_size)
    except:
        try:
            font = ImageFont.truetype("arial.ttf", font_size)
        except:
            font = ImageFont.load_default()

    # Create temporary image to measure text
    temp_img = Image.new('RGBA', (200, 50))
    temp_draw = ImageDraw.Draw(temp_img)
    bbox = temp_draw.textbbox((0, 0), text, font=font)
    text_width = bbox[2] - bbox[0]
    text_height = bbox[3] - bbox[1]

    # Calculate image size
    width = text_width + padding * 2
    height = text_height + padding * 2
    corner_radius = 6

    # Create image with transparency
    img = Image.new('RGBA', (width, height), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)

    # Draw rounded rectangle
    draw.rounded_rectangle(
        [(0, 0), (width - 1, height - 1)],
        radius=corner_radius,
        fill=bg_color
    )

    # Draw text centered
    text_x = (width - text_width) // 2
    text_y = (height - text_height) // 2 - 2  # Small adjustment for visual centering
    draw.text((text_x, text_y), text, fill=text_color, font=font)

    # Save
    output_path = os.path.join("Jellyfin.Plugin.BadgeFin/Assets", filename)
    os.makedirs(os.path.dirname(output_path), exist_ok=True)
    img.save(output_path, "PNG")
    print(f"Created: {output_path}")

def main():
    badges = [
        ("4K", (255, 193, 7, 255), (0, 0, 0, 255), "badge-4k.png"),      # Gold/Amber
        ("1080p", (224, 224, 224, 255), (0, 0, 0, 255), "badge-1080p.png"),  # Light gray
        ("720p", (158, 158, 158, 255), (0, 0, 0, 255), "badge-720p.png"),    # Medium gray
        ("SD", (97, 97, 97, 255), (255, 255, 255, 255), "badge-sd.png"),     # Dark gray
    ]

    for text, bg_color, text_color, filename in badges:
        create_badge(text, bg_color, text_color, filename)

    print("\nBadge generation complete!")
    print("You can customize the badges by replacing the PNG files in Assets/")

if __name__ == "__main__":
    main()
