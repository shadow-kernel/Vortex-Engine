#!/usr/bin/env python3
"""texture-solidify.py — edge padding for cut-out textures: spread the opaque colour of an RGBA image outwards into its
transparent texels so bilinear and mip filtering never pull the (black / white) background into leaf edges. In place.

    texture-solidify.py <image.png> [...]  [--passes 16]
"""
import argparse, os
from PIL import Image, ImageFilter


def solidify(img, passes):
    rgb = img.convert("RGB"); a = img.getchannel("A")
    solid = a.point(lambda v: 255 if v > 8 else 0)
    out = rgb.copy()
    for _ in range(passes):
        blurred = out.filter(ImageFilter.BoxBlur(1))
        out = Image.composite(out, blurred, solid)
        solid = solid.filter(ImageFilter.MaxFilter(3))
    out.putalpha(a)
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("images", nargs="+"); ap.add_argument("--passes", type=int, default=16)
    a = ap.parse_args()
    for p in a.images:
        img = Image.open(p)
        if img.mode != "RGBA":
            print(f"{os.path.basename(p)}: no alpha channel ({img.mode}) — skipped"); continue
        solidify(img, a.passes).save(p, optimize=True)
        print(f"{os.path.basename(p)}: padded {a.passes} texels")


if __name__ == "__main__":
    main()
