"""Draws the app icon (a rising-then-flat V/F curve on a dark squircle). Usage: python3 tools/make_icon.py src/GpuTweak.App/Assets/app.ico"""
import math, sys
from PIL import Image, ImageDraw, ImageFilter

S = 1024  # drawn at 1024 and scaled down for smooth edges
out = sys.argv[1]


def squircle_mask(size, n=5.0, pad=0.06):
    mask = Image.new("L", (size, size), 0)
    px = mask.load()
    r = size * (0.5 - pad)
    c = size / 2
    for y in range(size):
        for x in range(size):
            dx, dy = abs(x - c) / r, abs(y - c) / r
            if dx ** n + dy ** n <= 1:
                px[x, y] = 255
    return mask


def gradient(size, top, bottom):
    img = Image.new("RGB", (size, size))
    d = ImageDraw.Draw(img)
    for y in range(size):
        t = y / (size - 1)
        d.line([(0, y), (size, y)], fill=tuple(int(a + (b - a) * t) for a, b in zip(top, bottom)))
    return img


def curve_points():
    # Voltage on x, clock on y: a steep rise that flattens out, like a tuned V/F curve.
    pts = []
    x0, x1, y0, y1, knee = 0.20, 0.82, 0.74, 0.30, 0.56
    for i in range(1500):
        t = i / 1499
        x = x0 + (x1 - x0) * t
        if x < knee:
            u = (x - x0) / (knee - x0)
            y = y0 - (y0 - y1) * (1 - (1 - u) ** 2.2)
        else:
            y = y1
        pts.append((x * S, y * S))
    return pts


bg = gradient(S, (40, 42, 56), (12, 12, 16))
icon = Image.new("RGBA", (S, S), (0, 0, 0, 0))
icon.paste(bg, (0, 0), squircle_mask(S))


def layer(draw_fn, blur=0):
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    draw_fn(ImageDraw.Draw(img))
    return img.filter(ImageFilter.GaussianBlur(blur)) if blur else img


# Faint grid
icon = Image.alpha_composite(icon, layer(lambda d: [d.line([(S * 0.16, S * (0.16 + i * 0.12)), (S * 0.86, S * (0.16 + i * 0.12))],
                                                            fill=(255, 255, 255, 22), width=4) for i in range(1, 6)]))

pts = curve_points()
W = 56


def stroke(d, fill, width):
    # A round brush stamped along the curve; PIL's wide polylines leave notches at every joint.
    r = width / 2
    for p in pts:
        d.ellipse([p[0] - r, p[1] - r, p[0] + r, p[1] + r], fill=fill)


icon = Image.alpha_composite(icon, layer(lambda d: stroke(d, (48, 209, 88, 150), 80), blur=40))

# The curve, coloured green to blue from left to right through a mask.
mask = Image.new("L", (S, S), 0)
stroke(ImageDraw.Draw(mask), 255, W)
colours = Image.new("RGBA", (S, S))
cd = ImageDraw.Draw(colours)
for x in range(S):
    t = min(max((x - S * 0.2) / (S * 0.62), 0), 1)
    cd.line([(x, 0), (x, S)], fill=(int(48 + (10 - 48) * t), int(209 + (132 - 209) * t), int(88 + (255 - 88) * t), 255))
curve = Image.new("RGBA", (S, S), (0, 0, 0, 0))
curve.paste(colours, (0, 0), mask)
icon = Image.alpha_composite(icon, curve)

# Knee marker: the sweet spot
kx, ky = next(p for p in pts if p[0] >= S * 0.56)
icon = Image.alpha_composite(icon, layer(lambda d: (d.ellipse([kx - 52, ky - 52, kx + 52, ky + 52], fill=(255, 255, 255, 255)),
                                                    d.ellipse([kx - 27, ky - 27, kx + 27, ky + 27], fill=(48, 209, 88, 255)))))

icon = icon.resize((S // 2, S // 2), Image.LANCZOS)
sizes = [(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)]
icon.save(out, sizes=sizes)
