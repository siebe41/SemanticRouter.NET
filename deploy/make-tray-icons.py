#!/usr/bin/env python3
"""Makes the tray (menu-bar) icons from the app icon, src/SemanticRouter.Desktop/Assets/icon.png: its pulse glyph
without the dark tile, which nearly disappears on a dark menu bar or taskbar (#215).

- tray-template@2x.png (36 px, for an 18 pt menu bar): the glyph in black on transparent, for macOS, which
  draws a template image in the menu bar's own colours (light, dark, highlighted).
- tray.ico (16, 20, 24, 32, 40, 48 px): the glyph in its own teal and orange, for the Windows notification area,
  where the mid-tone colours read on light and dark taskbars alike.

Needs Pillow: python3 deploy/make-tray-icons.py
"""
from pathlib import Path

from PIL import Image

assets = Path(__file__).resolve().parent.parent / "src" / "SemanticRouter.Desktop" / "Assets"
tile = Image.open(assets / "icon.png").convert("RGBA")
w, h = tile.size
bg = tile.getpixel((w // 2, int(h * 0.82)))[:3]  # the tile's own dark colour, below the glyph

# How much each pixel differs from the tile is how much glyph it holds: the glyph's antialiased edge becomes alpha.
# The stroke's own difference from the tile: the median of the clearly-glyph pixels, so the teal line (closer to the
# tile than the orange dot) comes out solid too.
glyph = Image.new("RGBA", tile.size)
differences = sorted(
    d for d in (max(abs(c - k) for c, k in zip(tile.getpixel((x, y))[:3], bg)) for y in range(h) for x in range(w) if tile.getpixel((x, y))[3] == 255)
    if d > 60)
strongest = differences[len(differences) // 2] / 0.85
for y in range(h):
    for x in range(w):
        r, g, b, a = tile.getpixel((x, y))
        if a < 255:
            continue  # the tile's rounded corners
        coverage = min(1.0, max(abs(r - bg[0]), abs(g - bg[1]), abs(b - bg[2])) / (strongest * 0.85))
        if coverage < 0.08:
            continue
        # The colour without the tile mixed into the edge.
        colour = tuple(max(0, min(255, round((c - k * (1 - coverage)) / coverage))) for c, k in zip((r, g, b), bg))
        glyph.putpixel((x, y), (*colour, round(coverage * 255)))

# Square around the glyph, with a little room.
left, top, right, bottom = glyph.getbbox()
side = max(right - left, bottom - top)
pad = round(side * 0.02)
cx, cy = (left + right) // 2, (top + bottom) // 2
half = side // 2 + pad
glyph = glyph.crop((cx - half, cy - half, cx + half, cy + half))

black = Image.new("RGBA", glyph.size, (0, 0, 0, 0))
black.putalpha(glyph.getchannel("A"))
for size, name in ((36, "tray-template@2x.png"),):
    black.resize((size, size), Image.LANCZOS).save(assets / name)

sizes = [16, 20, 24, 32, 40, 48]
largest = glyph.resize((256, 256), Image.LANCZOS)
largest.save(assets / "tray.ico", sizes=[(s, s) for s in sizes])
print(f"Wrote tray-template@2x.png and tray.ico ({', '.join(map(str, sizes))} px) in {assets}")
