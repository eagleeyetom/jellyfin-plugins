#!/usr/bin/env python3
"""Fetch brand logos from Wikimedia Commons and turn them into JellyTag badges.

Raw Commons logos are sized and coloured for documents, not for a poster corner:
most are black-on-transparent, some have no viewBox, and a few are full colour.
This downloads each one and wraps it in the same rounded plate the generated
badge-*.svg assets use, so they drop straight into the existing renderer.

Always re-downloads, so it is safe to run repeatedly (wrapping is not additive).

Licences (verified via the Commons API):
  public domain : Dolby Vision, Dolby Atmos, Dolby TrueHD, HDR10, AV1, H.264, VP9, DTS-HD MA
  CC BY-SA 4.0  : HDR10+   -> requires attribution, see README
"""

import copy
import re
import time
import urllib.request
import xml.etree.ElementTree as ET
from pathlib import Path

SVG_NS = "http://www.w3.org/2000/svg"
ASSETS = Path(__file__).resolve().parent.parent / "Jellyfin.Plugin.JellyTag" / "Assets"
USER_AGENT = "JellyTag-plugin/1.0 (https://github.com/eagleeyetom/jellyfin-plugins)"
COMMONS = "https://upload.wikimedia.org/wikipedia/commons"

# name -> (source url, plate colour, force the artwork white)
# force_white is only safe for single-colour black artwork.
SOURCES = {
    "logo-dv.svg": (f"{COMMONS}/0/03/Dolby_Vision_2021_logo.svg", "#000000", True),
    "logo-atmos.svg": (f"{COMMONS}/8/82/Logo_Dolby_Atmos.svg", "#000000", True),
    "logo-truehd.svg": (f"{COMMONS}/9/90/Dolby_TrueHD.svg", "#000000", True),
    "logo-hdr10.svg": (f"{COMMONS}/9/94/HDR_10_logo_%28black%29.svg", "#000000", True),
    "logo-vp9.svg": (f"{COMMONS}/c/c7/Vp9-logo-for-mediawiki.svg", "#000000", True),
    # Full colour, so recolouring would destroy them. AV1 and DTS-HD MA are vivid
    # enough for a dark plate; HDR10+ and H.264 are near-black art and need a light one.
    "logo-av1.svg": (f"{COMMONS}/8/84/AV1_logo_2018.svg", "#000000", False),
    "logo-dtshdma.svg": (f"{COMMONS}/b/bd/DTS-HD-MA.svg", "#000000", False),
    "logo-hdr10plus.svg": (f"{COMMONS}/7/7c/HDR10%2B_Logo.svg", "#FFFFFF", False),
    "logo-h264.svg": (f"{COMMONS}/c/cd/H.264%2C_MPEG-4_AVC_logo.svg", "#FFFFFF", False),
}

BLACKS = {"#000", "#000000", "black", "#020202", "#010101", "#231f20", "#333", "#333333"}
BLACK_STYLE = re.compile(r"fill\s*:\s*(#000000|#000|black|#231f20|#333333|#333)\b", re.I)


def download(url):
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    with urllib.request.urlopen(request, timeout=30) as response:
        return response.read()


def dimensions(root):
    view_box = root.get("viewBox")
    if view_box:
        parts = [float(p) for p in re.split(r"[ ,]+", view_box.strip()) if p]
        if len(parts) == 4:
            return parts

    width = re.sub(r"[a-z%]+$", "", (root.get("width") or "0").strip(), flags=re.I)
    height = re.sub(r"[a-z%]+$", "", (root.get("height") or "0").strip(), flags=re.I)
    return [0.0, 0.0, float(width or 0), float(height or 0)]


def whiten(element):
    fill = element.get("fill")
    if fill and fill.strip().lower() in BLACKS:
        element.set("fill", "#FFFFFF")

    style = element.get("style")
    if style:
        element.set("style", BLACK_STYLE.sub("fill:#FFFFFF", style))

    if element.tag == f"{{{SVG_NS}}}style" and element.text:
        element.text = BLACK_STYLE.sub("fill:#FFFFFF", element.text)

    for child in element:
        whiten(child)


def normalize(raw, plate_colour, force_white):
    root = ET.fromstring(raw)
    min_x, min_y, width, height = dimensions(root)
    if width <= 0 or height <= 0:
        raise ValueError("could not determine dimensions")

    pad = height * 0.18
    plate_w = width + pad * 2
    plate_h = height + pad * 2

    new_root = ET.Element(f"{{{SVG_NS}}}svg", {
        "viewBox": f"0 0 {plate_w:g} {plate_h:g}",
        "width": f"{plate_w:g}",
        "height": f"{plate_h:g}",
        "version": "1.1",
    })
    ET.SubElement(new_root, f"{{{SVG_NS}}}rect", {
        "x": "0", "y": "0",
        "width": f"{plate_w:g}", "height": f"{plate_h:g}",
        "rx": f"{plate_h * 0.10:g}", "ry": f"{plate_h * 0.10:g}",
        "fill": plate_colour,
    })

    group = ET.SubElement(new_root, f"{{{SVG_NS}}}g", {
        "transform": f"translate({pad - min_x:g},{pad - min_y:g})",
    })
    if force_white:
        group.set("fill", "#FFFFFF")

    for child in list(root):
        group.append(copy.deepcopy(child))

    if force_white:
        whiten(group)

    return ET.tostring(new_root, encoding="utf-8", xml_declaration=True)


def main():
    ET.register_namespace("", SVG_NS)
    for index, (name, (url, plate_colour, force_white)) in enumerate(SOURCES.items()):
        if index:
            time.sleep(1.5)  # Commons returns 429 if fetched back to back.
        try:
            svg = normalize(download(url), plate_colour, force_white)
        except Exception as error:  # noqa: BLE001 - report and keep going
            print(f"FAILED  {name}: {error}")
            continue

        (ASSETS / name).write_bytes(svg)
        print(f"wrote   {name}  plate={plate_colour} white={force_white}")


if __name__ == "__main__":
    main()
