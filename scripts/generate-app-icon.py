#!/usr/bin/env python3
"""Generate the vector app icon and Windows ICO from one geometric design.

Development requirement: Pillow 10 or newer (`python -m pip install Pillow`).
The application itself does not need Python or Pillow at runtime.

Run from any directory: `python scripts/generate-app-icon.py`.
"""

from __future__ import annotations

import math
from pathlib import Path

from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "src" / "CondaManager.App" / "Assets"
SVG_PATH = ASSETS / "AppIcon.svg"
ICO_PATH = ASSETS / "AppIcon.ico"

CANVAS = 256
SUPERSAMPLE = 8
ICON_SIZES = (16, 24, 32, 48, 64, 128, 256)

BACKGROUND = "#0B3026"
BACKGROUND_EDGE = "#26664B"
ORBIT = "#8BE2AA"
CORE = "#DFF9E7"

SVG = f"""<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">
  <title>Conda Manager</title>
  <rect x="8" y="8" width="240" height="240" rx="54" fill="{BACKGROUND}" stroke="{BACKGROUND_EDGE}" stroke-width="3"/>
  <path d="M 177.104 75.343 A 72 72 0 1 0 177.104 180.657" fill="none" stroke="{ORBIT}" stroke-width="21" stroke-linecap="round"/>
  <path d="M 128 101 L 155 128 L 128 155 L 101 128 Z" fill="{CORE}"/>
</svg>
"""


def render_master() -> Image.Image:
    scale = SUPERSAMPLE
    image = Image.new("RGBA", (CANVAS * scale, CANVAS * scale), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    draw.rounded_rectangle(
        (8 * scale, 8 * scale, 248 * scale, 248 * scale),
        radius=54 * scale,
        fill=BACKGROUND,
        outline=BACKGROUND_EDGE,
        width=3 * scale,
    )

    # The long arc travels from upper right, around the left, to lower right.
    # Sampling only affects the ICO rasterization; the SVG uses a true arc.
    angles = (-47 - 266 * index / 512 for index in range(513))
    orbit_points = [
        (
            round((128 + 72 * math.cos(math.radians(angle))) * scale),
            round((128 + 72 * math.sin(math.radians(angle))) * scale),
        )
        for angle in angles
    ]
    draw.line(orbit_points, fill=ORBIT, width=21 * scale, joint="curve")
    radius = 10.5 * scale
    for x, y in (orbit_points[0], orbit_points[-1]):
        draw.ellipse((x - radius, y - radius, x + radius, y + radius), fill=ORBIT)

    draw.polygon(
        [(128 * scale, 101 * scale), (155 * scale, 128 * scale),
         (128 * scale, 155 * scale), (101 * scale, 128 * scale)],
        fill=CORE,
    )
    return image


def main() -> None:
    ASSETS.mkdir(parents=True, exist_ok=True)
    SVG_PATH.write_text(SVG, encoding="utf-8", newline="\n")
    master = render_master()
    master.save(ICO_PATH, format="ICO", sizes=[(size, size) for size in ICON_SIZES])

    with Image.open(ICO_PATH) as icon:
        actual = sorted(size[0] for size in icon.info["sizes"])
    if actual != list(ICON_SIZES):
        raise RuntimeError(f"ICO frames differ from requested sizes: {actual}")
    print(f"Wrote {SVG_PATH} and {ICO_PATH} with ICO frames {actual}")


if __name__ == "__main__":
    main()
