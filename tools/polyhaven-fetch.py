#!/usr/bin/env python3
"""Fetch CC0 assets from Poly Haven (https://polyhaven.com, CC0 1.0) into a Vortex project.

Models land as glTF (2k textures by default) under Assets/Models/<group>/<id>/<id>_<res>.gltf with their textures
beside them; texture sets land under Assets/Textures/<id>/ with a ready .vmat in Assets/Materials/<id>.vmat
(albedo / normal (DirectX) / roughness / AO, world-space tiling from the set's real size). Leaf materials whose glTF
diffuse carries no alpha get the set's *_alpha mask merged into a PNG and the material switched to alpha-test, so
trees and plants cut out correctly on every backend.

  python3 tools/polyhaven-fetch.py --project <dir> model pine_tree_01 fir_tree_01 --group Trees [--res 2k]
  python3 tools/polyhaven-fetch.py --project <dir> texture forest_floor grass_ground --res 2k [--size 4]
  python3 tools/polyhaven-fetch.py --project <dir> hdri kloppenheim_06 --res 2k

Writes/updates <project>/ATTRIBUTIONS_polyhaven.md with every id it fetched. Needs Pillow for the alpha merge.
"""
import argparse, json, os, sys, time, urllib.request, urllib.error

API = "https://api.polyhaven.com"
UA = "VortexEngine-polyhaven-fetch/1.0 (+https://github.com/shadow-kernel/Vortex-Engine)"


def get_json(url):
    req = urllib.request.Request(url, headers={"User-Agent": UA})
    with urllib.request.urlopen(req, timeout=60) as r:
        return json.loads(r.read().decode("utf-8"))


def download(url, dest, size=None):
    os.makedirs(os.path.dirname(dest), exist_ok=True)
    if os.path.exists(dest) and (size is None or os.path.getsize(dest) == size):
        return False
    req = urllib.request.Request(url, headers={"User-Agent": UA})
    for attempt in range(3):
        try:
            with urllib.request.urlopen(req, timeout=120) as r, open(dest + ".part", "wb") as f:
                while True:
                    chunk = r.read(1 << 20)
                    if not chunk: break
                    f.write(chunk)
            os.replace(dest + ".part", dest)
            return True
        except (urllib.error.URLError, TimeoutError) as e:
            if attempt == 2: raise
            time.sleep(2 * (attempt + 1))
    return True


def pick(files, key, res, prefer=("jpg", "png", "exr")):
    """files[key][res][fmt] -> (fmt, entry) with the preferred format that exists."""
    node = files.get(key, {}).get(res)
    if not node: return None, None
    for fmt in prefer:
        if fmt in node: return fmt, node[fmt]
    fmt = next(iter(node))
    return fmt, node[fmt]


def fetch_model(project, asset_id, group, res, log):
    files = get_json(f"{API}/files/{asset_id}")
    info = get_json(f"{API}/info/{asset_id}")
    name = info.get("name", asset_id)
    out_dir = os.path.join(project, "Assets", "Models", group, asset_id)
    gltf = files.get("gltf", {}).get(res)
    fbx = files.get("fbx", {}).get(res)
    if gltf:
        entry = gltf["gltf"]
        gltf_path = os.path.join(out_dir, os.path.basename(entry["url"]))
        download(entry["url"], gltf_path, entry.get("size"))
        for inc_name, inc in entry.get("include", {}).items():
            download(inc["url"], os.path.join(out_dir, inc_name), inc.get("size"))
        merge_leaf_alpha(asset_id, files, res, out_dir, gltf_path, log)
        rel = os.path.relpath(gltf_path, project).replace("\\", "/")
    elif fbx:
        entry = fbx["fbx"]
        fbx_path = os.path.join(out_dir, os.path.basename(entry["url"]))
        download(entry["url"], fbx_path, entry.get("size"))
        for inc_name, inc in entry.get("include", {}).items():
            download(inc["url"], os.path.join(out_dir, inc_name), inc.get("size"))
        rel = os.path.relpath(fbx_path, project).replace("\\", "/")
    else:
        log.append(f"- {asset_id}: no glTF / FBX at {res} — skipped")
        return None
    log.append(f"- **{name}** (`{asset_id}`) — model, `{rel}`")
    return rel


def merge_leaf_alpha(asset_id, files, res, out_dir, gltf_path, log):
    """Poly Haven exports the diffuse as JPG; a leaf material's cut-out lives in <id>_<material>_alpha. Merge it into a
    PNG and point the glTF material at it with alphaMode MASK — the engine reads that as AlphaTest."""
    alpha_keys = [k for k in files if k.endswith("_alpha") or k == "alpha"]
    if not alpha_keys: return
    try:
        from PIL import Image
    except ImportError:
        log.append(f"  - {asset_id}: has alpha masks ({', '.join(alpha_keys)}) but Pillow is missing — leaves stay opaque")
        return
    with open(gltf_path, "r", encoding="utf-8") as f:
        doc = json.load(f)
    images = doc.get("images", [])
    textures = doc.get("textures", [])
    materials = doc.get("materials", [])
    changed = False
    for key in alpha_keys:
        prefix = key[:-len("_alpha")] if key != "alpha" else ""
        fmt, entry = pick(files, key, res, ("png", "jpg"))
        if not entry: continue
        mask_path = os.path.join(out_dir, os.path.basename(entry["url"]))
        download(entry["url"], mask_path, entry.get("size"))
        # the diffuse image of that material: <id>_<prefix>_diff_<res>.jpg
        diff_name = f"{asset_id}_{prefix}_diff_{res}.jpg" if prefix else f"{asset_id}_diff_{res}.jpg"
        diff_path = os.path.join(out_dir, diff_name)
        if not os.path.exists(diff_path): continue
        png_name = diff_name[:-4] + "_rgba.png"
        png_path = os.path.join(out_dir, png_name)
        if not os.path.exists(png_path):
            diff = Image.open(diff_path).convert("RGB")
            mask = Image.open(mask_path).convert("L").resize(diff.size)
            diff.putalpha(mask)
            diff.save(png_path, optimize=True)
        for img in images:
            if img.get("uri") == diff_name:
                img["uri"] = png_name
                changed = True
        # every material sampling that image becomes a cut-out
        img_index = [i for i, img in enumerate(images) if img.get("uri") == png_name]
        tex_index = [i for i, t in enumerate(textures) if t.get("source") in img_index]
        for m in materials:
            bct = m.get("pbrMetallicRoughness", {}).get("baseColorTexture", {})
            if bct.get("index") in tex_index:
                m["alphaMode"] = "MASK"; m["alphaCutoff"] = 0.5; m["doubleSided"] = True
                changed = True
    if changed:
        with open(gltf_path, "w", encoding="utf-8") as f:
            json.dump(doc, f, indent=1)
        log.append(f"  - {asset_id}: leaf alpha merged into PNG, materials set to MASK / double-sided")


MAP_KEYS = {
    "albedo": ("diff", "diffuse", "col", "color"),
    "normal": ("nor_dx", "nor_gl", "normal"),
    "roughness": ("rough", "roughness"),
    "ao": ("ao",),
    "disp": ("disp", "displacement", "height"),
    "arm": ("arm",),
}


def fetch_texture(project, asset_id, res, size_m, log):
    files = get_json(f"{API}/files/{asset_id}")
    info = get_json(f"{API}/info/{asset_id}")
    name = info.get("name", asset_id)
    out_dir = os.path.join(project, "Assets", "Textures", asset_id)
    found = {}
    for slot, keys in MAP_KEYS.items():
        for k in keys:
            if k in files:
                fmt, entry = pick(files, k, res, ("jpg", "png"))
                if entry:
                    dest = os.path.join(out_dir, os.path.basename(entry["url"]))
                    download(entry["url"], dest, entry.get("size"))
                    found[slot] = os.path.relpath(dest, project).replace("\\", "/")
                    break
    if "albedo" not in found:
        log.append(f"- {asset_id}: no diffuse map at {res} — skipped")
        return None
    # the set's real-world size (Poly Haven lists it in the dimensions tag, e.g. "2x2m")
    real = size_m
    dims = info.get("dimensions") or []
    if not real and dims:
        try: real = float(str(dims[0]).lower().replace("m", "").split("x")[0])
        except ValueError: real = None
    if not real: real = 2.0
    mat_dir = os.path.join(project, "Assets", "Materials")
    os.makedirs(mat_dir, exist_ok=True)
    mat_path = os.path.join(mat_dir, asset_id + ".vmat")
    rel_tex = lambda p: os.path.relpath(os.path.join(project, p), mat_dir).replace("\\", "/")
    vmat = {
        "Name": asset_id, "Version": "2.0", "BaseColor": [1, 1, 1, 1],
        "Metallic": 0, "Roughness": 1 if "roughness" in found or "arm" in found else 0.85, "AmbientOcclusion": 1, "NormalStrength": 1, "HeightScale": 0.0,
        "UseDirectXNormals": "nor_dx" in found.get("normal", "") or "nor_gl" not in found.get("normal", ""),
        "EmissiveStrength": 0.0, "TwoSided": False, "BlendMode": "Opaque", "CastShadows": True, "ReceiveShadows": True,
        "AlbedoTexture": rel_tex(found["albedo"]),
        "UVTiling": [1, 1], "UVOffset": [0, 0], "RealWorldSize": [real, real],
    }
    if "normal" in found: vmat["NormalTexture"] = rel_tex(found["normal"])
    if "roughness" in found: vmat["RoughnessTexture"] = rel_tex(found["roughness"])
    elif "arm" in found: vmat["OcclusionRoughnessMetallicTexture"] = rel_tex(found["arm"])
    if "ao" in found: vmat["AOTexture"] = rel_tex(found["ao"])
    if "disp" in found: vmat["HeightTexture"] = rel_tex(found["disp"])
    with open(mat_path, "w", encoding="utf-8") as f:
        json.dump(vmat, f, indent=2)
    rel = os.path.relpath(mat_path, project).replace("\\", "/")
    log.append(f"- **{name}** (`{asset_id}`) — texture set, `{rel}` ({real} m)")
    return rel


def fetch_hdri(project, asset_id, res, log):
    files = get_json(f"{API}/files/{asset_id}")
    info = get_json(f"{API}/info/{asset_id}")
    name = info.get("name", asset_id)
    node = files.get("hdri", {}).get(res)
    if not node:
        log.append(f"- {asset_id}: no HDRI at {res} — skipped")
        return None
    fmt, entry = pick(files, "hdri", res, ("hdr", "exr"))
    dest = os.path.join(project, "Assets", "Skies", os.path.basename(entry["url"]))
    download(entry["url"], dest, entry.get("size"))
    rel = os.path.relpath(dest, project).replace("\\", "/")
    log.append(f"- **{name}** (`{asset_id}`) — HDRI, `{rel}`")
    return rel


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--project", required=True, help="the Vortex project folder (holds project.vortex / Assets)")
    ap.add_argument("kind", choices=["model", "texture", "hdri"])
    ap.add_argument("ids", nargs="+", help="Poly Haven asset ids")
    ap.add_argument("--res", default="2k", help="1k, 2k, 4k (default 2k)")
    ap.add_argument("--group", default="PolyHaven", help="models: the folder under Assets/Models")
    ap.add_argument("--size", type=float, default=None, help="textures: real-world size in metres (default: Poly Haven's)")
    args = ap.parse_args()
    log = []
    results = []
    for asset_id in args.ids:
        try:
            if args.kind == "model": results.append(fetch_model(args.project, asset_id, args.group, args.res, log))
            elif args.kind == "texture": results.append(fetch_texture(args.project, asset_id, args.res, args.size, log))
            else: results.append(fetch_hdri(args.project, asset_id, args.res, log))
            print("fetched", asset_id, "->", results[-1])
        except Exception as e:
            print("FAILED", asset_id, e, file=sys.stderr)
            log.append(f"- {asset_id}: FAILED ({e})")
    att = os.path.join(args.project, "ATTRIBUTIONS_polyhaven.md")
    header = "# Poly Haven assets (CC0 1.0, https://polyhaven.com/license)\n\nFetched with tools/polyhaven-fetch.py — no attribution required, kept here as a record.\n\n"
    old = open(att, encoding="utf-8").read() if os.path.exists(att) else header
    with open(att, "w", encoding="utf-8") as f:
        f.write(old.rstrip("\n") + "\n" + "\n".join(log) + "\n")
    print(f"{sum(1 for r in results if r)} / {len(args.ids)} assets in {args.project}")


if __name__ == "__main__":
    main()
