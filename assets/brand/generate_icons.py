"""Renders the red Guardian shield to PNG/ICO/ICNS. Mirrors logo.svg. Run: python3 generate_icons.py <out_dir>"""
import sys
from PIL import Image, ImageDraw

RED, DARK, WHITE = (211, 47, 47, 255), (183, 28, 28, 115), (255, 255, 255, 255)

def bezier(p0, p1, p2, p3, n=40):
    return [tuple((1-t)**3*a + 3*(1-t)**2*t*b + 3*(1-t)*t**2*c + t**3*d for a, b, c, d in zip(p0, p1, p2, p3))
            for t in (i / n for i in range(n + 1))]

def shield(s):
    k = s / 64
    pts = [(32, 4), (56, 12), (56, 30)] + bezier((56, 30), (56, 45), (46, 55), (32, 60)) + bezier((32, 60), (18, 55), (8, 45), (8, 30)) + [(8, 12)]
    return [(x * k, y * k) for x, y in pts]

def render(size):
    big = size * 4
    img = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.polygon(shield(big), fill=RED)
    k = big / 64
    right = [(32, 8.2), (52.5, 15), (52.5, 30)] + bezier((52.5, 30), (52.5, 42.6), (44.2, 51.4), (32, 56))
    overlay = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    ImageDraw.Draw(overlay).polygon([(x * k, y * k) for x, y in right], fill=DARK)
    img = Image.alpha_composite(img, overlay)
    d = ImageDraw.Draw(img)
    check = [(21 * k, 32.5 * k), (28.5 * k, 40 * k), (43.5 * k, 24.5 * k)]
    w = int(6 * k)
    d.line(check, fill=WHITE, width=w, joint="curve")
    for x, y in (check[0], check[-1]):
        d.ellipse([x - w / 2, y - w / 2, x + w / 2, y + w / 2], fill=WHITE)
    return img.resize((size, size), Image.LANCZOS)

if __name__ == "__main__":
    out = sys.argv[1] if len(sys.argv) > 1 else "."
    master = render(1024)
    master.save(f"{out}/logo-1024.png")
    render(256).save(f"{out}/logo-256.png")
    render(64).save(f"{out}/logo-64.png")
    master.save(f"{out}/guardian.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
    master.save(f"{out}/guardian.icns")
