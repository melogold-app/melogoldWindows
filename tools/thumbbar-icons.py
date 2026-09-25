"""Иконки кнопок на миниатюре в панели задач (⏮ ⏯ ⏭) из Segoe Fluent Icons: светлые и тёмные, 16–32 px.
Запуск: python tools/thumbbar-icons.py → src/Melogold.App/Assets/Thumbbar/*.ico"""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

FONT = Path("C:/Windows/Fonts/SegoeIcons.ttf")
GLYPHS = {"previous": "\uE892", "play": "\uE768", "pause": "\uE769", "next": "\uE893"}
COLORS = {"light": (255, 255, 255, 255), "dark": (0, 0, 0, 230)}
SIZES = [16, 20, 24, 32]
OUT = Path(__file__).resolve().parent.parent / "src" / "Melogold.App" / "Assets" / "Thumbbar"
OUT.mkdir(parents=True, exist_ok=True)

for name, glyph in GLYPHS.items():
    for theme, color in COLORS.items():
        frames = []
        for size in SIZES:
            # Рисуем крупно и уменьшаем: края мягче
            big = size * 8
            image = Image.new("RGBA", (big, big), (0, 0, 0, 0))
            draw = ImageDraw.Draw(image)
            font = ImageFont.truetype(str(FONT), int(big * 0.8))
            box = draw.textbbox((0, 0), glyph, font=font)
            x = (big - (box[2] - box[0])) / 2 - box[0]
            y = (big - (box[3] - box[1])) / 2 - box[1]
            draw.text((x, y), glyph, font=font, fill=color)
            frames.append(image.resize((size, size), Image.LANCZOS))
        frames[-1].save(OUT / f"{name}-{theme}.ico", sizes=[(s, s) for s in SIZES], append_images=frames[:-1])
print("saved", OUT)
