"""Generate original WhalePaper app icon and default wallpaper.

No external/copyrighted art is used. Run from the repository root:
    python tools/make_assets.py
"""

from __future__ import annotations

import math
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "src" / "WhalePaper" / "Assets"
ASSETS.mkdir(parents=True, exist_ok=True)


def lerp(a: int, b: int, t: float) -> int:
    return int(round(a + (b - a) * t))


def gradient(size: tuple[int, int], top: tuple[int, int, int], bottom: tuple[int, int, int]) -> Image.Image:
    w, h = size
    img = Image.new("RGB", size)
    px = img.load()
    for y in range(h):
        t = y / max(1, h - 1)
        color = (lerp(top[0], bottom[0], t), lerp(top[1], bottom[1], t), lerp(top[2], bottom[2], t))
        for x in range(w):
            px[x, y] = color
    return img


def bezier(points, steps=120):
    """Simple De Casteljau sampler for a list of control points."""
    out = []
    for i in range(steps + 1):
        t = i / steps
        pts = list(points)
        while len(pts) > 1:
            nxt = []
            for a, b in zip(pts, pts[1:]):
                nxt.append((a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t))
            pts = nxt
        out.append(pts[0])
    return out


def whale_polygon(cx: float, cy: float, scale: float):
    """Return a clean, stylized whale silhouette.

    Draw order matters: tail, body, fin. The returned list is kept for
    compatibility with the earlier wallpaper code, but callers should use
    draw_whale() so the pieces merge without internal seams.
    """
    tail = [
        (cx - 86 * scale, cy + 4 * scale),
        (cx - 150 * scale, cy - 56 * scale),
        (cx - 136 * scale, cy - 4 * scale),
        (cx - 162 * scale, cy + 11 * scale),
        (cx - 136 * scale, cy + 30 * scale),
        (cx - 150 * scale, cy + 66 * scale),
    ]
    fin = [
        (cx - 34 * scale, cy + 30 * scale),
        (cx - 2 * scale, cy + 82 * scale),
        (cx + 48 * scale, cy + 33 * scale),
    ]
    body = [
        (cx - 104 * scale, cy + 4 * scale),
        (cx + 104 * scale, cy + 4 * scale),
    ]
    return tail + fin + body


def draw_whale(draw: ImageDraw.ImageDraw, cx: float, cy: float, scale: float,
               fill: tuple[int, int, int, int], eye: tuple[int, int, int, int] | None = None,
               spout: tuple[int, int, int, int] | None = None) -> None:
    """Draw a smooth whale from overlapping shapes (tail, body, fin)."""
    tail = [
        (cx - 84 * scale, cy + 2 * scale),
        (cx - 150 * scale, cy - 56 * scale),
        (cx - 135 * scale, cy - 3 * scale),
        (cx - 161 * scale, cy + 12 * scale),
        (cx - 135 * scale, cy + 30 * scale),
        (cx - 150 * scale, cy + 66 * scale),
    ]
    fin = [
        (cx - 36 * scale, cy + 29 * scale),
        (cx - 4 * scale, cy + 82 * scale),
        (cx + 50 * scale, cy + 32 * scale),
    ]
    draw.polygon(tail, fill=fill)
    draw.ellipse((cx - 106 * scale, cy - 49 * scale, cx + 106 * scale, cy + 49 * scale), fill=fill)
    draw.polygon(fin, fill=fill)
    # belly shading: a subtle darker band along the bottom
    shade = (max(0, fill[0] - 42), max(0, fill[1] - 32), max(0, fill[2] - 28), int(fill[3] * 0.55))
    draw.arc((cx - 102 * scale, cy - 8 * scale, cx + 102 * scale, cy + 70 * scale), 18, 162, fill=shade, width=max(2, int(4 * scale)))
    # eye
    if eye is not None:
        ex, ey = cx + 63 * scale, cy - 13 * scale
        er = max(2.0, 5.5 * scale)
        draw.ellipse((ex - er, ey - er, ex + er, ey + er), fill=eye)
    # water spout
    if spout is not None:
        sx, sy = cx + 9 * scale, cy - 47 * scale
        sh = 40 * scale
        draw.line([(sx, sy), (sx, sy - sh)], fill=spout, width=max(2, int(5 * scale)))
        draw.arc((sx - 30 * scale, sy - sh - 18 * scale, sx, sy - sh + 18 * scale), 290, 360, fill=spout, width=max(2, int(4 * scale)))
        draw.arc((sx, sy - sh - 22 * scale, sx + 30 * scale, sy - sh + 18 * scale), 180, 250, fill=spout, width=max(2, int(4 * scale)))


def make_icon() -> None:
    size = 1024
    img = gradient((size, size), (16, 86, 99), (4, 16, 24)).convert("RGBA")
    # soft glow
    glow = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    gd = ImageDraw.Draw(glow)
    gd.ellipse((-180, 380, 760, 1280), fill=(60, 224, 205, 70))
    glow = glow.filter(ImageFilter.GaussianBlur(150))
    img = Image.alpha_composite(img, glow)
    draw = ImageDraw.Draw(img)
    # subtle circle
    draw.ellipse((150, 150, 874, 874), outline=(130, 245, 229, 45), width=5)
    draw_whale(draw, 505, 505, 2.25, (242, 255, 252, 255), (10, 48, 55, 255), (160, 250, 238, 230))
    # round icon mask
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, size - 1, size - 1), radius=230, fill=255)
    img.putalpha(mask)
    img = img.resize((256, 256), Image.LANCZOS)
    img.save(ASSETS / "app.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
    img.save(ASSETS / "app.png")


def make_wallpaper() -> None:
    w, h = 1920, 1080
    img = gradient((w, h), (7, 30, 40), (3, 10, 17)).convert("RGBA")
    glow = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    gd = ImageDraw.Draw(glow)
    gd.ellipse((-450, 120, 1150, 1500), fill=(29, 125, 133, 75))
    gd.ellipse((900, -500, 2150, 600), fill=(15, 78, 112, 70))
    glow = glow.filter(ImageFilter.GaussianBlur(220))
    img = Image.alpha_composite(img, glow)
    draw = ImageDraw.Draw(img, "RGBA")

    # layered wave lines
    for i in range(7):
        y0 = 760 + i * 34
        amp = 36 + i * 7
        pts = []
        for x in range(-50, w + 60, 18):
            y = y0 + math.sin(x / (210 + i * 20) + i * 0.8) * amp
            pts.append((x, y))
        draw.line(pts, fill=(85, 220, 208, max(18, 80 - i * 8)), width=2 + i // 3)

    # large faint whale
    overlay = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    od = ImageDraw.Draw(overlay)
    draw_whale(od, 1320, 560, 2.9, (111, 232, 220, 34))
    overlay = overlay.filter(ImageFilter.GaussianBlur(3))
    img = Image.alpha_composite(img, overlay)

    # small bright whale
    draw = ImageDraw.Draw(img, "RGBA")
    draw_whale(draw, 780, 430, 1.5, (178, 250, 241, 205), (8, 35, 42, 255), (178, 250, 241, 170))

    # stars / particles
    import random
    random.seed(7)
    for _ in range(90):
        x = random.randint(0, w)
        y = random.randint(0, 650)
        r = random.choice([1, 1, 1, 2])
        a = random.randint(20, 100)
        draw.ellipse((x, y, x + r, y + r), fill=(190, 255, 248, a))

    # bottom vignette
    vign = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    vd = ImageDraw.Draw(vign)
    for y in range(h - 320, h):
        t = (y - (h - 320)) / 320
        vd.line([(0, y), (w, y)], fill=(0, 6, 10, int(110 * t)))
    img = Image.alpha_composite(img, vign)
    img.convert("RGB").save(ASSETS / "default-wallpaper.png", optimize=True)
    img.convert("RGB").resize((960, 540), Image.LANCZOS).save(ROOT / "docs" / "preview-default.jpg", quality=88)


if __name__ == "__main__":
    (ROOT / "docs").mkdir(exist_ok=True)
    make_icon()
    make_wallpaper()
    print("assets generated")
