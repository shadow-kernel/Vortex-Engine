#!/usr/bin/env python3
"""texture-detile.py — take the low-frequency brightness drift out of a tiling texture so its repeats stop reading as a
grid on big terrains (a photo-scanned sand tile is darker at its edges than in the middle: every 7 m square shows).
Divides the image by a wide Gaussian blur of itself and restores the original mean. In place; keeps the format.

    texture-detile.py <albedo.jpg> [...] [--radius 0.12]   # radius as a fraction of the image size
"""
import argparse, os
from PIL import Image, ImageFilter, ImageChops


def detile(img, radius_frac):
    rgb = img.convert("RGB")
    w, h = rgb.size
    # wrap-around blur: tile 3×3 so the blur sees the seamless neighbours, blur, take the centre
    big = Image.new("RGB", (w * 3, h * 3))
    for i in range(3):
        for j in range(3): big.paste(rgb, (i * w, j * h))
    r = max(2, int(min(w, h) * radius_frac))
    small = big.resize((big.width // 8, big.height // 8), Image.BOX).filter(ImageFilter.GaussianBlur(r / 8)).resize(big.size, Image.BILINEAR)
    low = small.crop((w, h, 2 * w, 2 * h))
    out = Image.new("RGB", (w, h))
    src = rgb.load(); lo = low.load(); dst = out.load()
    mean = [0, 0, 0]
    px = rgb.resize((64, 64), Image.BOX).getdata()
    for p in px:
        for c in range(3): mean[c] += p[c]
    mean = [m / (64 * 64) for m in mean]
    for y in range(h):
        for x in range(w):
            s = src[x, y]; l = lo[x, y]
            dst[x, y] = tuple(min(255, max(0, int(s[c] * mean[c] / max(1.0, l[c])))) for c in range(3))
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("images", nargs="+"); ap.add_argument("--radius", type=float, default=0.12)
    a = ap.parse_args()
    for p in a.images:
        img = Image.open(p)
        out = detile(img, a.radius)
        out.save(p, quality=92) if p.lower().endswith((".jpg", ".jpeg")) else out.save(p)
        print(f"{os.path.basename(p)}: flattened")


if __name__ == "__main__":
    main()
