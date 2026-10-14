"""Рисует иконку SelfLock: щит с фиолетово-синим градиентом и белым замком."""
import sys
from PIL import Image, ImageDraw

S = 1024


def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(len(a)))


def shield_mask(size):
    m = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(m)
    w = size

    def quad(p0, p1, p2, n=40):
        return [((1 - t) ** 2 * p0[0] + 2 * (1 - t) * t * p1[0] + t * t * p2[0],
                 (1 - t) ** 2 * p0[1] + 2 * (1 - t) * t * p1[1] + t * t * p2[1])
                for t in (i / n for i in range(n + 1))]

    P = lambda x, y: (x * w, y * w)
    pts = []
    pts += quad(P(0.14, 0.17), P(0.34, 0.15), P(0.5, 0.05))   # верх слева
    pts += quad(P(0.5, 0.05), P(0.66, 0.15), P(0.86, 0.17))   # верх справа
    pts += [P(0.86, 0.48)]
    pts += quad(P(0.86, 0.48), P(0.84, 0.80), P(0.5, 0.96))   # правый бок к острию
    pts += quad(P(0.5, 0.96), P(0.16, 0.80), P(0.14, 0.48))   # левый бок
    d.polygon(pts, fill=255)
    return m


def main(out):
    grad = Image.new("RGBA", (S, S))
    gd = ImageDraw.Draw(grad)
    c1, c2 = (150, 92, 255, 255), (64, 140, 255, 255)
    for y in range(S):
        gd.line([(0, y), (S, y)], fill=lerp(c1, c2, y / S))
    mask = shield_mask(S)
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    img.paste(grad, (0, 0), mask)

    # блик
    hl = Image.new("L", (S, S), 0)
    ImageDraw.Draw(hl).ellipse([-0.2 * S, -0.55 * S, 1.0 * S, 0.45 * S], fill=60)
    from PIL import ImageChops
    hl = ImageChops.multiply(hl, mask)
    white = Image.new("RGBA", (S, S), (255, 255, 255, 255))
    img.paste(white, (0, 0), hl)

    d = ImageDraw.Draw(img)
    # дужка замка
    cx = S // 2
    d.rounded_rectangle([cx - 150, 300, cx + 150, 600], radius=150, outline=(255, 255, 255, 255), width=62)
    # корпус
    d.rounded_rectangle([cx - 210, 450, cx + 210, 760], radius=70, fill=(255, 255, 255, 255))
    # скважина
    d.ellipse([cx - 42, 540, cx + 42, 624], fill=(98, 110, 255, 255))
    d.rounded_rectangle([cx - 20, 590, cx + 20, 690], radius=14, fill=(98, 110, 255, 255))

    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    img.save(out, format="ICO", sizes=[(s, s) for s in sizes])
    img.resize((256, 256), Image.LANCZOS).save(out.replace(".ico", ".png"))


if __name__ == "__main__":
    main(sys.argv[1])
