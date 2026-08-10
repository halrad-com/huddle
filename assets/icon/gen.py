"""Huddle app icon generator.

Geometry is defined once here and emitted as both SVG (vector master)
and PNG (supersampled raster) so the two can never drift apart.
Canvas is 512x512 with the mark centred at (256, 256).
"""
import math
import os
from PIL import Image, ImageDraw

C = 256.0          # centre
CANVAS = 512

PALETTE_8 = [
    "#E2593B",  # vermilion
    "#EE9B33",  # amber
    "#C99A22",  # gold
    "#57A83E",  # green
    "#2FA189",  # teal
    "#3C8CCE",  # blue
    "#7B60D4",  # violet
    "#BF57A6",  # magenta
]
PALETTE_7 = [PALETTE_8[i] for i in (0, 1, 3, 4, 5, 6, 7)]
PALETTE_6 = [PALETTE_8[i] for i in (0, 1, 3, 4, 5, 6)]
CENTRE = "#5E6672"  # neutral slate: visible on both light and dark grounds


def ring(n, palette, ring_r, dot_r, centre_r, start_deg=-90.0,
         r_jitter=None, size_jitter=None):
    """Return a list of (cx, cy, r, colour) circles, centre dot last."""
    circles = []
    for i in range(n):
        ang = math.radians(start_deg + i * 360.0 / n)
        rr = ring_r + (r_jitter[i] if r_jitter else 0.0)
        dr = dot_r + (size_jitter[i] if size_jitter else 0.0)
        circles.append((C + rr * math.cos(ang), C + rr * math.sin(ang),
                        dr, palette[i % len(palette)]))
    if centre_r:
        circles.append((C, C, centre_r, CENTRE))
    return circles


# ------------------------------------------------------------------ the mark
# Primary: 7 dots around a dominant neutral centre. The odd count and the
# heavy centre keep it from reading as a colour wheel or a loading spinner.
FULL = ring(7, PALETTE_7, ring_r=156, dot_r=60, centre_r=70)

# Small-size variant: below ~32px the 7-dot ring turns to mush, so drop to
# 5 fatter dots. Same idea, same palette order, survives 16px.
SMALL = ring(5, [PALETTE_8[i] for i in (0, 1, 3, 5, 6)],
             ring_r=152, dot_r=74, centre_r=76)

# Sizes at or below this threshold use SMALL.
SMALL_MAX = 24


def mark_for(size):
    return SMALL if size <= SMALL_MAX else FULL


def to_svg(circles, title):
    body = "\n".join(
        f'  <circle cx="{cx:.2f}" cy="{cy:.2f}" r="{r:.2f}" fill="{col}"/>'
        for cx, cy, r, col in circles)
    return (
        f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {CANVAS} {CANVAS}" '
        f'width="{CANVAS}" height="{CANVAS}" role="img" aria-label="{title}">\n'
        f'  <title>{title}</title>\n{body}\n</svg>\n')


def to_png(circles, size, path, ss=8):
    """Supersampled render - ss=8 gives clean edges even at 16px."""
    big = size * ss
    scale = big / CANVAS
    img = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    for cx, cy, r, col in circles:
        x, y, rr = cx * scale, cy * scale, r * scale
        d.ellipse([x - rr, y - rr, x + rr, y + rr], fill=col)
    img.resize((size, size), Image.LANCZOS).save(path)


def to_png_opaque(circles, size, path, bg=(255, 255, 255)):
    """Flattened onto a solid ground - for apple-touch-icon, which must
    not carry alpha (iOS composites transparency to black)."""
    tmp = f"{path}.tmp.png"
    to_png(circles, size, tmp)
    fg = Image.open(tmp).convert("RGBA")
    out = Image.new("RGB", (size, size), bg)
    out.paste(fg, (0, 0), fg)
    out.save(path)
    os.remove(tmp)


PNG_SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 192, 256, 512, 1024]
ICO_SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
FAVICON_SIZES = [16, 24, 32, 48]


def build_ico(path, sizes):
    """Pillow's ICO writer downsamples one source image; we want each
    frame rendered at its own size (and the small ones simplified), so
    the frames are assembled by hand."""
    frames = []
    for s in sizes:
        img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
        tmp = f"/tmp/_ico_{s}.png"
        to_png(mark_for(s), s, tmp)
        frames.append(Image.open(tmp).convert("RGBA"))
    frames.sort(key=lambda i: i.size[0], reverse=True)
    frames[0].save(path, format="ICO",
                   sizes=[(f.size[0], f.size[1]) for f in frames],
                   append_images=frames[1:])


if __name__ == "__main__":
    root = os.path.dirname(os.path.abspath(__file__))
    for d in ("svg", "png", "web", "windows"):
        os.makedirs(os.path.join(root, d), exist_ok=True)

    open(f"{root}/svg/huddle.svg", "w").write(to_svg(FULL, "Huddle"))
    open(f"{root}/svg/huddle-small.svg", "w").write(
        to_svg(SMALL, "Huddle (small-size variant)"))

    for s in PNG_SIZES:
        to_png(mark_for(s), s, f"{root}/png/huddle-{s}.png")

    build_ico(f"{root}/windows/huddle.ico", ICO_SIZES)
    build_ico(f"{root}/web/favicon.ico", FAVICON_SIZES)

    for s in (16, 32, 192, 512):
        to_png(mark_for(s), s, f"{root}/web/icon-{s}.png")
    to_png_opaque(FULL, 180, f"{root}/web/apple-touch-icon.png",
                  bg=(255, 255, 255))

    print("built")
