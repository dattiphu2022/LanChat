"""Rebuild the LanChat icon and suggestion artwork with Pillow.

All artwork is drawn from the geometry in this file. It uses no input
images, fonts, emoji glyphs, or external asset downloads.
"""

from math import cos, sin, tau
from pathlib import Path

from PIL import Image, ImageDraw


ASSETS = Path(__file__).resolve().parents[1] / "src" / "LanChat.WinForms" / "Assets"
SCALE = 4
SIZE = 80
FRAMES = 16
PAPER = "#fffdf8"


def rounded(draw, box, radius, color):
    draw.rounded_rectangle(tuple(round(v * SCALE) for v in box), radius * SCALE, color)


def polygon(draw, points, color):
    draw.polygon([(round(x * SCALE), round(y * SCALE)) for x, y in points], fill=color)


def dot(draw, x, y, radius, color):
    rounded(draw, (x - radius, y - radius, x + radius, y + radius), radius, color)


def sparkle(draw, x, y, radius, color):
    polygon(draw, [(x + cos(i * tau / 8) * radius * (1 if i % 2 == 0 else 0.32),
                    y + sin(i * tau / 8) * radius * (1 if i % 2 == 0 else 0.32))
                   for i in range(8)], color)


def chat_bubble(draw, box, tail, color):
    rounded(draw, box, 8, color)
    polygon(draw, tail, color)


def hello(draw, phase):
    chat_bubble(draw, (15, 28, 63, 55), [(24, 51), (23, 64), (36, 54)], "#176766")
    for index, x in enumerate((28, 39, 50)):
        dot(draw, x, 41 + 2 * sin(phase - index), 2.6, PAPER)
    sparkle(draw, 61, 21, 5 + 2 * cos(phase), "#c28134")


def celebrate(draw, phase):
    polygon(draw, [(20, 64), (31, 33), (52, 52)], "#bf5a3b")
    polygon(draw, [(25, 50), (28, 42), (42, 56), (34, 59)], "#f4b55b")
    for index, (x, y, color) in enumerate(((46, 27, "#176766"), (63, 35, "#a74570"),
                                           (36, 21, "#a74570"), (55, 17, "#b67526"),
                                           (62, 52, "#176766"))):
        sparkle(draw, x + 2 * cos(phase + index), y + 3 * sin(phase + index), 3.8, color)


def birthday(draw, phase):
    rounded(draw, (15, 58, 65, 63), 2, "#71506a")
    rounded(draw, (19, 39, 61, 59), 5, "#b84d77")
    rounded(draw, (19, 39, 61, 47), 4, "#ffe0b4")
    rounded(draw, (37, 29, 43, 40), 2, "#176766")
    x = 40 + 1.8 * sin(phase)
    polygon(draw, [(x, 16), (x + 4, 23), (40, 28), (36, 24)], "#b84b35")
    sparkle(draw, 54, 25, 3 + cos(phase), "#b67526")


def love(draw, phase):
    radius = 1.05 + 0.11 * cos(phase)
    points = []
    for index in range(64):
        angle = index * tau / 64
        x = 16 * sin(angle) ** 3
        y = 13 * cos(angle) - 5 * cos(2 * angle) - 2 * cos(3 * angle) - cos(4 * angle)
        points.append((39 + x * radius, 40 - y * radius))
    polygon(draw, points, "#a63563")
    sparkle(draw, 61, 24, 3.5 + 1.4 * sin(phase), "#b67526")
    sparkle(draw, 20, 56, 3 + 1.3 * cos(phase), "#a63563")


def save_gif(name, background, paint):
    frames = []
    for index in range(FRAMES):
        image = Image.new("RGB", (SIZE * SCALE, SIZE * SCALE), PAPER)
        draw = ImageDraw.Draw(image)
        rounded(draw, (3, 3, 77, 77), 16, background)
        paint(draw, index * tau / FRAMES)
        frames.append(image.resize((SIZE, SIZE), Image.Resampling.LANCZOS))
    frames[0].save(
        ASSETS / "Suggestions" / name, save_all=True, append_images=frames[1:],
        duration=80, loop=0, disposal=2,
    )


def save_icon():
    image = Image.new("RGBA", (SIZE * SCALE, SIZE * SCALE))
    draw = ImageDraw.Draw(image)
    rounded(draw, (2, 2, 78, 78), 19, "#153f41")
    chat_bubble(draw, (32, 34, 68, 58), [(52, 55), (61, 66), (61, 55)], "#86d4bd")
    chat_bubble(draw, (12, 18, 58, 46), [(20, 43), (19, 56), (32, 45)], "#fff3d5")
    for x in (24, 35, 46):
        dot(draw, x, 32, 2.6, "#153f41")
    image.save(ASSETS / "LanChat.ico", sizes=[(n, n) for n in (16, 24, 32, 48, 64, 128, 256)])


if __name__ == "__main__":
    (ASSETS / "Suggestions").mkdir(exist_ok=True)
    save_icon()
    save_gif("Xin-chao.gif", "#e0f3ee", hello)
    save_gif("Chuc-mung.gif", "#faecd1", celebrate)
    save_gif("Sinh-nhat.gif", "#f8e5ee", birthday)
    save_gif("Yeu.gif", "#f7e1e7", love)
