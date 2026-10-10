#!/usr/bin/env python3
"""polyhaven-leaf-alpha.py — give a fetched Poly Haven plant its cut-out: Poly Haven exports the diffuse maps as JPG (no
alpha) and ships each leaf material's mask as <id>_<material>_alpha. For every model folder given, this downloads the masks
(2k by default), merges each into an RGBA PNG next to the JPG — with the leaf colour DILATED into the transparent area so
bilinear / mip filtering never bleeds the JPG's background into the edges (the "white speckles") — and points every glTF in
the folder (the original and the *_lod one) at the PNG with alphaMode MASK, cutoff 0.5, double-sided.

    polyhaven-leaf-alpha.py <project>/Assets/Models/Oasis/island_tree_01 [...more folders] [--res 2k]

needs: Pillow
"""
import argparse, json, os, sys, urllib.request
from PIL import Image, ImageFilter

API = "https://api.polyhaven.com"


def get(url):
    with urllib.request.urlopen(urllib.request.Request(url, headers={"User-Agent": "vortex-leaf-alpha"}), timeout=60) as r:
        return r.read()


def dilate_colour(rgb, alpha, passes=12):
    """Spread the opaque colour outwards into the transparent texels (edge padding)."""
    rgb = rgb.convert("RGB"); a = alpha.convert("L")
    solid = a.point(lambda v: 255 if v > 8 else 0)
    out = rgb.copy()
    for _ in range(passes):
        blurred = out.filter(ImageFilter.BoxBlur(1))
        grown = solid.filter(ImageFilter.MaxFilter(3))
        # take the blurred colour only where we were transparent and now have a neighbour
        ring = Image.eval(grown, lambda v: v) ; ring = Image.composite(Image.new("L", a.size, 255), Image.new("L", a.size, 0), grown)
        out = Image.composite(out, blurred, solid)   # keep the real colour inside, blurred outside
        solid = grown
    return out


def process(folder, res):
    asset_id = os.path.basename(os.path.normpath(folder))
    files = json.loads(get(f"{API}/files/{asset_id}"))
    alpha_keys = [k for k in files if k.endswith("_alpha") or k == "alpha"]
    if not alpha_keys:
        print(f"{asset_id}: no alpha masks on Poly Haven — nothing to do"); return
    tex_dir = os.path.join(folder, "textures"); os.makedirs(tex_dir, exist_ok=True)
    replaced = {}
    for key in alpha_keys:
        prefix = key[:-len("_alpha")] if key != "alpha" else ""
        entry = files[key].get(res) or files[key].get("2k") or next(iter(files[key].values()))
        fmt = "png" if "png" in entry else next(iter(entry))
        url = entry[fmt]["url"]
        mask_path = os.path.join(tex_dir, f"{asset_id}_{prefix + '_' if prefix else ''}alpha_{res}.{fmt}")
        if not os.path.exists(mask_path):
            open(mask_path, "wb").write(get(url)); print(f"  downloaded {os.path.basename(mask_path)}")
        diff_name = f"{asset_id}_{prefix + '_' if prefix else ''}diff_{res}.jpg"
        diff_path = os.path.join(tex_dir, diff_name)
        if not os.path.exists(diff_path):
            print(f"  {diff_name} not found — skipped"); continue
        diff = Image.open(diff_path).convert("RGB"); mask = Image.open(mask_path).convert("L")
        if mask.size != diff.size: mask = mask.resize(diff.size, Image.BILINEAR)
        rgba = dilate_colour(diff, mask); rgba.putalpha(mask)
        png_name = diff_name[:-4] + "_rgba.png"
        rgba.save(os.path.join(tex_dir, png_name), optimize=True)
        replaced["textures/" + diff_name] = "textures/" + png_name
        print(f"  {diff_name} + mask -> {png_name}")
    for name in os.listdir(folder):
        if not name.endswith(".gltf"): continue
        path = os.path.join(folder, name); g = json.load(open(path)); changed = False
        img_by_index = {}
        for i, im in enumerate(g.get("images", [])):
            if im.get("uri") in replaced:
                im["uri"] = replaced[im["uri"]]; im["mimeType"] = "image/png"; img_by_index[i] = True; changed = True
        tex_with_alpha = {ti for ti, t in enumerate(g.get("textures", [])) if t.get("source") in img_by_index}
        for m in g.get("materials", []):
            bc = m.get("pbrMetallicRoughness", {}).get("baseColorTexture", {}).get("index")
            if bc in tex_with_alpha:
                m["alphaMode"] = "MASK"; m["alphaCutoff"] = 0.5; m["doubleSided"] = True; changed = True
        if changed:
            json.dump(g, open(path, "w"), separators=(",", ":")); print(f"  {name}: materials set to MASK / double-sided")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("folders", nargs="+"); ap.add_argument("--res", default="2k")
    a = ap.parse_args()
    for f in a.folders:
        print(os.path.basename(os.path.normpath(f)) + ":")
        try: process(f, a.res)
        except Exception as e: print(f"  failed: {e}")


if __name__ == "__main__":
    main()
