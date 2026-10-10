#!/usr/bin/env python3
"""texture-tint.py — multiply an albedo texture per channel in LINEAR light (sRGB decoded, scaled, re-encoded): turn a
pinkish beach sand golden, cool a rock, without touching its detail. Alpha is kept. In place.

    texture-tint.py <image> [...] --rgb 1.03 0.95 0.72
"""
import argparse, os
from PIL import Image


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("images", nargs="+"); ap.add_argument("--rgb", type=float, nargs=3, required=True)
    a = ap.parse_args()
    luts = []
    for gain in a.rgb:
        luts.append(bytes(min(255, int(round(255.0 * min(1.0, ((v / 255.0) ** 2.2) * gain) ** (1.0 / 2.2)))) for v in range(256)))
    lut = luts[0] + luts[1] + luts[2]
    for p in a.images:
        img = Image.open(p)
        alpha = img.getchannel("A") if img.mode == "RGBA" else None
        rgb = img.convert("RGB").point(lut)
        if alpha is not None: rgb.putalpha(alpha)
        rgb.save(p, quality=92) if p.lower().endswith((".jpg", ".jpeg")) else rgb.save(p, optimize=True)
        print(f"{os.path.basename(p)}: x({a.rgb[0]}, {a.rgb[1]}, {a.rgb[2]}) linear")


if __name__ == "__main__":
    main()
