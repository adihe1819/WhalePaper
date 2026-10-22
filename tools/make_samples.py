"""Create small local test media for WhalePaper. Not shipped with the app."""

from __future__ import annotations

import math
import shutil
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
SAMPLES = ROOT / "samples"
SAMPLES.mkdir(exist_ok=True)

# Reuse the generated default wallpaper as a static image sample.
default = ROOT / "src" / "WhalePaper" / "Assets" / "default-wallpaper.png"
shutil.copyfile(default, SAMPLES / "test-static.png")
Image.open(default).convert("RGB").save(SAMPLES / "test-static.jpg", quality=88)

# Static WebP.
Image.open(default).convert("RGB").resize((1280, 720), Image.LANCZOS).save(
    SAMPLES / "test-static.webp", quality=82
)

# Animated GIF: dark ocean with moving waves and a small whale.
w, h = 640, 360
frames = []
for i in range(30):
    img = Image.new("RGB", (w, h), (6, 24, 33))
    draw = ImageDraw.Draw(img)
    for band in range(5):
        pts = []
        for x in range(-20, w + 20, 10):
            y = 210 + band * 26 + math.sin(x / 50 + i / 5 + band) * (12 + band * 4)
            pts.append((x, y))
        draw.line(pts, fill=(30 + band * 10, 110 + band * 12, 112 + band * 15), width=2)
    # whale body
    bx = 250 + int(math.sin(i / 7) * 55)
    by = 150 + int(math.cos(i / 9) * 12)
    draw.ellipse((bx, by, bx + 150, by + 58), fill=(103, 231, 214))
    draw.polygon([(bx, by + 26), (bx - 48, by - 18), (bx - 35, by + 25), (bx - 50, by + 58)], fill=(103, 231, 214))
    draw.polygon([(bx + 50, by + 45), (bx + 80, by + 88), (bx + 112, by + 48)], fill=(103, 231, 214))
    draw.ellipse((bx + 105, by + 14, bx + 117, by + 26), fill=(6, 24, 33))
    draw.line([(bx + 34, by - 8), (bx + 34, by - 34)], fill=(150, 245, 235), width=4)
    frames.append(img)
frames[0].save(
    SAMPLES / "test-animated.gif",
    save_all=True,
    append_images=frames[1:],
    duration=60,
    loop=0,
    optimize=False,
)
print("samples generated in", SAMPLES)
