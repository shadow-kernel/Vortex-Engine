#!/usr/bin/env python3
"""texture-brighten.py — scale an albedo texture in LINEAR light (sRGB decoded, multiplied, re-encoded, clamped); alpha is
kept. For plant textures that were authored too dark for a physically based renderer (a frond at 0.03 albedo reads black
under a desert sun; real leaves sit at 0.08 – 0.15). In place.

    texture-brighten.py <image> [...] --gain 2.5
"""
import argparse, os
from PIL import Image


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("images", nargs="+"); ap.add_argument("--gain", type=float, required=True)
    a = ap.parse_args()
    lut = bytes(min(255, int(round(255.0 * min(1.0, ((v / 255.0) ** 2.2) * a.gain) ** (1.0 / 2.2)))) for v in range(256))
    for p in a.images:
        img = Image.open(p)
        alpha = img.getchannel("A") if img.mode == "RGBA" else None
        rgb = img.convert("RGB").point(lut * 3)
        if alpha is not None: rgb.putalpha(alpha)
        rgb.save(p, quality=92) if p.lower().endswith((".jpg", ".jpeg")) else rgb.save(p, optimize=True)
        print(f"{os.path.basename(p)}: x{a.gain} (linear)")


if __name__ == "__main__":
    main()
