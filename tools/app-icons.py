"""
Иконки приложения Windows — скруглённая плитка, как у значков Windows 11 и на странице организации:
белая (или своего цвета) плитка со скруглёнными углами (радиус 22 % стороны) и рисунок на ней.

    python tools/app-icons.py tools/melogold-tile.png src/Melogold.App/Assets/melogold.ico src/Melogold.App/Assets/melogold-256.png 256
    python tools/app-icons.py <исходник.png> <куда.ico> [<куда.png> <размер>] [--square]

Исходник — плитка 512×512 (прозрачные углы уже есть) или квадрат без скругления (--square: углы срезаются здесь).
В .ico — все размеры, которые берёт Windows: 16, 20, 24, 32, 40, 48, 64, 96, 128, 256.
"""
import sys
from PIL import Image, ImageDraw

SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
RADIUS = 0.22


def rounded(image: Image.Image) -> Image.Image:
    """Углы срезаются по скруглённому прямоугольнику; маска рисуется крупно и сжимается — край без лесенки."""
    image = image.convert('RGBA')
    size = image.size[0]
    scale = 4
    mask = Image.new('L', (size * scale, size * scale), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, size * scale - 1, size * scale - 1), radius=int(size * scale * RADIUS), fill=255)
    mask = mask.resize((size, size), Image.LANCZOS)
    combined = Image.composite(image.getchannel('A'), Image.new('L', (size, size), 0), mask)
    image.putalpha(combined)
    return image


def main(argv: list[str]) -> None:
    square = '--square' in argv
    args = [a for a in argv if a != '--square']
    source = Image.open(args[0]).convert('RGBA')
    if source.size[0] != source.size[1]:
        raise SystemExit('source must be square')
    tile = rounded(source) if square else source
    tile.resize((256, 256), Image.LANCZOS).save(args[1], format='ICO', sizes=[(s, s) for s in SIZES])
    if len(args) >= 4:
        size = int(args[3])
        tile.resize((size, size), Image.LANCZOS).save(args[2])
    print('ok', args[1])


if __name__ == '__main__':
    main(sys.argv[1:])
