#!/usr/bin/env python3
"""Makes the tray (menu-bar) icons from the app icon's vector source, deploy/icon.svg: its pulse line and dot without
the dark tile, which nearly disappears on a dark menu bar or taskbar (#215).

- tray-template@2x.png (36 px, for an 18 pt menu bar): the glyph in black on transparent, for macOS, which
  draws a template image in the menu bar's own colours (light, dark, highlighted).
- tray.ico (16, 20, 24, 32, 40, 48 px): the glyph in its own teal and orange, for the Windows notification area,
  where the mid-tone colours read on light and dark taskbars alike.
- tray.png (48 px): the same for Linux. A StatusNotifier host does not tell the app what colour its panel is, and the
  desktop's light or dark setting does not say either (GNOME's top bar is dark in both), so it gets the colours that
  read on both.

Each size is drawn from the shapes, not scaled down from a large image: the stroke is a whole number of pixels and the
flat start of the line sits on the pixel grid, so it stays sharp at 16 px.

Needs Pillow: python3 deploy/make-tray-icons.py
"""
import math
import xml.etree.ElementTree as ET
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw

deploy = Path(__file__).resolve().parent
assets = deploy.parent / "src" / "SemanticRouter.Desktop" / "Assets"
SUPERSAMPLE = 16
# Pillow fills a shape's edge pixels on both sides, which makes it one (supersampled) pixel too wide: each shape is
# drawn half a pixel smaller all round.
EDGE = 0.5

shapes = {e.get("id"): e for e in ET.parse(deploy / "icon.svg").getroot() if e.get("id")}
line, dot, tile = shapes["line"], shapes["dot"], shapes["tile"]
points = [tuple(map(float, p.split(","))) for p in line.get("points").split()]
stroke = float(line.get("stroke-width"))
centre, radius = (float(dot.get("cx")), float(dot.get("cy"))), float(dot.get("r"))


def colour(hex_colour):
    return tuple(int(hex_colour[i:i + 2], 16) for i in (1, 3, 5))


def coverage(size, scale, offset, draw_shape):
    """A shape's coverage of each pixel, from drawing it SUPERSAMPLE times larger and averaging."""
    big = Image.new("L", (size * SUPERSAMPLE, size * SUPERSAMPLE))
    k = scale * SUPERSAMPLE
    draw_shape(ImageDraw.Draw(big), lambda x, y: ((x * scale + offset[0]) * SUPERSAMPLE, (y * scale + offset[1]) * SUPERSAMPLE), k)
    return big.resize((size, size), Image.BOX)


def draw_line(width):
    def draw(d, at, k):
        r = width * k / 2 - EDGE
        for (x1, y1), (x2, y2) in zip(points, points[1:]):
            (ax, ay), (bx, by) = at(x1, y1), at(x2, y2)
            length = math.hypot(bx - ax, by - ay)
            nx, ny = (ay - by) / length * r, (bx - ax) / length * r
            d.polygon([(ax + nx, ay + ny), (bx + nx, by + ny), (bx - nx, by - ny), (ax - nx, ay - ny)], fill=255)
        for x, y in points:  # round caps and joins
            cx, cy = at(x, y)
            d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=255)
    return draw


def draw_dot(d, at, k):
    cx, cy = at(*centre)
    r = radius * k - EDGE
    d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=255)


def draw_tile(d, at, k):
    (x0, y0), (x1, y1) = at(0, 0), at(float(tile.get("width")), float(tile.get("height")))
    d.rounded_rectangle((x0 + EDGE, y0 + EDGE, x1 - EDGE, y1 - EDGE), radius=float(tile.get("rx")) * k - EDGE, fill=255)


def paint(layers, size):
    """Lays (coverage, RGB) layers over each other on transparent."""
    image = Image.new("RGBA", (size, size))
    for mask, rgb in layers:
        layer = Image.new("RGBA", (size, size), (*rgb, 255))
        layer.putalpha(mask)
        image.alpha_composite(layer)
    return image


# The glyph's extent with its round ends, in the SVG's units, and a square around it with a little room.
left = min(min(x for x, _ in points) - stroke / 2, centre[0] - radius)
right = max(max(x for x, _ in points) + stroke / 2, centre[0] + radius)
top = min(min(y for _, y in points) - stroke / 2, centre[1] - radius)
bottom = max(max(y for _, y in points) + stroke / 2, centre[1] + radius)
side = max(right - left, bottom - top) * 1.04
flat = points[0][1]  # the height of the line's flat start


def glyph(size, black=False):
    scale = size / side
    width_px = max(2, round(stroke * scale))  # a whole number of pixels, and never thinner than 2
    offset = [size / 2 - (left + right) / 2 * scale, size / 2 - (top + bottom) / 2 * scale]
    edge = flat * scale + offset[1] - width_px / 2
    offset[1] += round(edge) - edge  # the flat start's top and bottom edges on the pixel grid
    line_mask = coverage(size, scale, offset, draw_line(width_px / scale))
    dot_mask = coverage(size, scale, offset, draw_dot)
    if black:
        return paint([(line_mask, (0, 0, 0)), (dot_mask, (0, 0, 0))], size)
    return paint([(line_mask, colour(line.get("stroke"))), (dot_mask, colour(dot.get("fill")))], size)


glyph(36, black=True).save(assets / "tray-template@2x.png")
sizes = [16, 20, 24, 32, 40, 48]
frames = [glyph(s) for s in sizes]
frames[-1].save(assets / "tray.ico", sizes=[(s, s) for s in sizes], append_images=frames[:-1])
glyph(48).save(assets / "tray.png")
print(f"Wrote tray-template@2x.png, tray.ico ({', '.join(map(str, sizes))} px) and tray.png in {assets}")

# icon.svg is the app icon's source only while it still draws icon.png.
icon = Image.open(assets / "icon.png").convert("RGBA")
n = icon.size[0]
drawn = paint([(coverage(n, n / 256, (0, 0), draw_tile), colour(tile.get("fill"))),
               (coverage(n, n / 256, (0, 0), draw_line(stroke)), colour(line.get("stroke"))),
               (coverage(n, n / 256, (0, 0), draw_dot), colour(dot.get("fill")))], n)
difference = ImageChops.difference(drawn, icon).convert("L")
changed = sum(difference.histogram()[49:]) / (n * n)
print(f"icon.svg drawn at {n} px differs from icon.png in {changed:.2%} of its pixels")
if changed > 0.01:
    raise SystemExit("icon.svg no longer matches icon.png: update one to match the other")
