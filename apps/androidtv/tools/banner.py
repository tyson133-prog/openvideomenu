"""Draws the Android TV launcher banner (res/drawable-xhdpi/banner.png, 640x360).

    python apps/androidtv/tools/banner.py

Near-black with a warm glow from the top left, the app's name in gold serif, a thin gold rule and a
tagline. Needs Pillow and a serif font (Georgia on Windows; DejaVu Serif elsewhere).
"""
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont

NAME = "ovmplayer"
TAGLINE = "menus for your library"
W, H = 640, 360
GOLD = (255, 208, 110)
RULE = (204, 165, 84)
TEXT = (214, 210, 202)
OUT = Path(__file__).resolve().parents[1] / "app/src/main/res/drawable-xhdpi/banner.png"


def font(size: int) -> ImageFont.FreeTypeFont:
    for name in ("C:/Windows/Fonts/georgia.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSerif.ttf", "DejaVuSerif.ttf"):
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            continue
    raise SystemExit("No serif font found (Georgia or DejaVu Serif).")


image = Image.new("RGB", (W, H), (4, 4, 4))
glow = Image.new("RGB", (W, H), (0, 0, 0))
ImageDraw.Draw(glow).ellipse((-220, -220, 160, 160), fill=(70, 46, 14))
image = Image.blend(image, glow.filter(ImageFilter.GaussianBlur(90)), 0.55)

draw = ImageDraw.Draw(image)
title = font(78)
tag = font(26)
box = draw.textbbox((0, 0), NAME, font=title)
draw.text(((W - (box[2] - box[0])) / 2 - box[0], 118 - box[1]), NAME, font=title, fill=GOLD)
draw.line((150, 222, 490, 222), fill=RULE, width=3)
box = draw.textbbox((0, 0), TAGLINE, font=tag)
draw.text(((W - (box[2] - box[0])) / 2 - box[0], 244 - box[1]), TAGLINE, font=tag, fill=TEXT)

image.save(OUT)
print("wrote", OUT)
