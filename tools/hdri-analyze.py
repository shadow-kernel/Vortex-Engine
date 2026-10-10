#!/usr/bin/env python3
"""Analyse an equirectangular Radiance .hdr sky for scene lighting: the sun's direction and colour (the brightest patch),
the sky's average colour by elevation band (top / horizon / ground) and an exposure suggestion — the numbers a scene's
sun light, sky gradient and ambient are set from so the HDRI and the lighting agree.

  python3 tools/hdri-analyze.py Assets/Skies/goegap_2k.hdr [--json]

Pure Python (a Radiance RGBE reader, RLE and flat); downsamples to ~512 px wide for speed.
"""
import argparse, json, math, struct, sys

def read_hdr(path):
    with open(path, "rb") as f:
        data = f.read()
    pos = data.index(b"\n\n") + 2
    line_end = data.index(b"\n", pos)
    dims = data[pos:line_end].decode("ascii").split()
    h, w = int(dims[1]), int(dims[3])   # "-Y h +X w"
    p = line_end + 1
    pixels = bytearray(w * h * 4)
    for y in range(h):
        row = y * w * 4
        if data[p] == 2 and data[p + 1] == 2 and (data[p + 2] << 8 | data[p + 3]) == w:
            p += 4
            for c in range(4):
                x = 0
                while x < w:
                    count = data[p]; p += 1
                    if count > 128:
                        count -= 128; val = data[p]; p += 1
                        for k in range(count): pixels[row + (x + k) * 4 + c] = val
                        x += count
                    else:
                        for k in range(count): pixels[row + (x + k) * 4 + c] = data[p + k]
                        p += count; x += count
        else:
            pixels[row:row + w * 4] = data[p:p + w * 4]; p += w * 4
    return w, h, pixels

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("hdr"); ap.add_argument("--json", action="store_true")
    a = ap.parse_args()
    w, h, px = read_hdr(a.hdr)
    step = max(1, w // 512)
    def rgb(x, y):
        i = (y * w + x) * 4
        e = px[i + 3]
        if e == 0: return (0.0, 0.0, 0.0)
        f = math.ldexp(1.0, e - 136)
        return (px[i] * f, px[i + 1] * f, px[i + 2] * f)
    # bands by elevation; the brightest pixel (with a small neighbourhood) is the sun
    bands = {"top": [0, 0, 0, 0], "horizon": [0, 0, 0, 0], "ground": [0, 0, 0, 0]}
    best = (-1.0, 0, 0)
    total = [0.0, 0.0, 0.0]; n = 0
    for y in range(0, h, step):
        elev = 90.0 - 180.0 * (y + 0.5) / h          # +90 top .. -90 bottom
        band = "top" if elev > 35 else ("horizon" if elev > -8 else "ground")
        wgt = math.cos(math.radians(elev))
        for x in range(0, w, step):
            r, g, b = rgb(x, y)
            lum = 0.2126 * r + 0.7152 * g + 0.0722 * b
            bb = bands[band]; bb[0] += r * wgt; bb[1] += g * wgt; bb[2] += b * wgt; bb[3] += wgt
            total[0] += r * wgt; total[1] += g * wgt; total[2] += b * wgt; n += wgt
            if lum > best[0] and elev > -5: best = (lum, x, y)
    out = {}
    for k, v in bands.items():
        out[k] = [round(v[i] / max(v[3], 1e-6), 4) for i in range(3)]
    avg = [t / max(n, 1e-6) for t in total]
    # the sun: average the 1% brightest pixels around the peak for colour, direction from the peak
    lum, sx, sy = best
    azimuth = 360.0 * (sx + 0.5) / w              # 0 at the image's left edge, increasing to the right
    elevation = 90.0 - 180.0 * (sy + 0.5) / h
    sr, sg, sb = rgb(sx, sy)
    m = max(sr, sg, sb, 1e-6)
    sun_color = [round(sr / m, 3), round(sg / m, 3), round(sb / m, 3)]
    # a direction vector the engine's directional light uses (pointing FROM the sun): x = east? equirect u=0 at -X? convention: u = (atan2(dir.x, -dir.z) / 2π + 0.5)
    phi = math.radians(azimuth); th = math.radians(elevation)
    sun_dir_from = [round(-math.cos(th) * math.sin(phi), 4), round(-math.sin(th), 4), round(math.cos(th) * math.cos(phi), 4)]
    out.update({
        "width": w, "height": h, "average": [round(v, 4) for v in avg],
        "sun": {"azimuth_deg": round(azimuth, 2), "elevation_deg": round(elevation, 2), "luminance": round(lum, 1), "color": sun_color, "direction_from_sun": sun_dir_from},
        "exposure_suggestion": round(min(4.0, max(0.25, 0.9 / max(avg[1], 1e-3))), 3),
    })
    if a.json: print(json.dumps(out, indent=1))
    else:
        print(f"{a.hdr}: {w}x{h}")
        print(f"  sun: azimuth {out['sun']['azimuth_deg']}°, elevation {out['sun']['elevation_deg']}°, colour {sun_color}, peak luminance {out['sun']['luminance']}")
        print(f"  sky top {out['top']}  horizon {out['horizon']}  ground {out['ground']}  average {out['average']}")
        print(f"  exposure suggestion {out['exposure_suggestion']}")

if __name__ == "__main__":
    main()
