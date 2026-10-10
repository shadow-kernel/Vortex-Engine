#!/usr/bin/env python3
"""Convert a textured Wavefront OBJ (one material: diffuse / normal textures beside it) into a glTF 2.0 file the engine
imports with a proper cut-out material — for CC0 packs that ship OBJ + TGA (e.g. Yughues' palms).

  python3 tools/obj2gltf.py <model.obj> <out.gltf> --diffuse diffuse.tga [--normal normal.tga] [--scale 1.0] [--cutout] [--name Palm]

Writes <out>.gltf + <out>.bin + the textures as PNG next to it (TGA converted; the diffuse keeps its alpha). Flips
the V coordinate (OBJ is bottom-up, glTF top-down). Needs Pillow.
"""
import argparse, json, os, struct, sys

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("obj"); ap.add_argument("out")
    ap.add_argument("--diffuse", required=True); ap.add_argument("--normal", default=None)
    ap.add_argument("--scale", type=float, default=1.0); ap.add_argument("--cutout", action="store_true")
    ap.add_argument("--name", default=None); ap.add_argument("--yup", action="store_true", help="the OBJ is already Y-up (default: Z-up like 3ds Max exports) — set when the model stands up without it")
    a = ap.parse_args()
    from PIL import Image
    pos, nrm, uv, faces = [], [], [], []
    with open(a.obj, encoding="utf-8", errors="ignore") as f:
        for line in f:
            t = line.split()
            if not t: continue
            if t[0] == "v": pos.append((float(t[1]), float(t[2]), float(t[3])))
            elif t[0] == "vn": nrm.append((float(t[1]), float(t[2]), float(t[3])))
            elif t[0] == "vt": uv.append((float(t[1]), float(t[2]) if len(t) > 2 else 0.0))
            elif t[0] == "f":
                idx = []
                for v in t[1:]:
                    p = v.split("/")
                    vi = int(p[0]) - 1
                    ti = int(p[1]) - 1 if len(p) > 1 and p[1] else -1
                    ni = int(p[2]) - 1 if len(p) > 2 and p[2] else -1
                    idx.append((vi, ti, ni))
                for k in range(1, len(idx) - 1): faces.append((idx[0], idx[k], idx[k + 1]))
    # unique vertices per (v, vt, vn)
    verts, index, keymap = [], [], {}
    for tri in faces:
        for key in tri:
            if key not in keymap:
                keymap[key] = len(verts); verts.append(key)
            index.append(keymap[key])
    def P(i):
        x, y, z = pos[i]
        if not a.yup: x, y, z = x, z, -y      # Z-up -> Y-up
        return (x * a.scale, y * a.scale, z * a.scale)
    def N(i):
        if i < 0 or i >= len(nrm): return (0.0, 1.0, 0.0)
        x, y, z = nrm[i]
        if not a.yup: x, y, z = x, z, -y
        return (x, y, z)
    def T(i):
        if i < 0 or i >= len(uv): return (0.0, 0.0)
        u, v = uv[i]; return (u, 1.0 - v)
    # flat normals when the OBJ has none
    if not nrm:
        acc = [[0.0, 0.0, 0.0] for _ in verts]
        for k in range(0, len(index), 3):
            i0, i1, i2 = index[k], index[k + 1], index[k + 2]
            p0, p1, p2 = P(verts[i0][0]), P(verts[i1][0]), P(verts[i2][0])
            u = (p1[0] - p0[0], p1[1] - p0[1], p1[2] - p0[2]); w = (p2[0] - p0[0], p2[1] - p0[1], p2[2] - p0[2])
            n = (u[1] * w[2] - u[2] * w[1], u[2] * w[0] - u[0] * w[2], u[0] * w[1] - u[1] * w[0])
            for i in (i0, i1, i2):
                for c in range(3): acc[i][c] += n[c]
    pbuf, nbuf, tbuf = bytearray(), bytearray(), bytearray()
    mins = [1e30] * 3; maxs = [-1e30] * 3
    for vi, (pi, ti, ni) in enumerate(verts):
        p = P(pi)
        for c in range(3): mins[c] = min(mins[c], p[c]); maxs[c] = max(maxs[c], p[c])
        pbuf += struct.pack("<3f", *p)
        if nrm: n = N(ni)
        else:
            n = acc[vi]; l = (n[0] ** 2 + n[1] ** 2 + n[2] ** 2) ** 0.5 or 1.0; n = (n[0] / l, n[1] / l, n[2] / l)
        nbuf += struct.pack("<3f", *n)
        tbuf += struct.pack("<2f", *T(ti))
    ibuf = bytearray()
    for i in index: ibuf += struct.pack("<I", i)
    out_dir = os.path.dirname(os.path.abspath(a.out)); os.makedirs(out_dir, exist_ok=True)
    base = os.path.splitext(os.path.basename(a.out))[0]
    bin_name = base + ".bin"
    blob = bytes(pbuf) + bytes(nbuf) + bytes(tbuf) + bytes(ibuf)
    with open(os.path.join(out_dir, bin_name), "wb") as f: f.write(blob)
    # textures -> PNG
    images, textures, samplers = [], [], [{"magFilter": 9729, "minFilter": 9987, "wrapS": 10497, "wrapT": 10497}]
    def add_tex(src, name, keep_alpha):
        im = Image.open(src)
        im = im.convert("RGBA") if keep_alpha and im.mode in ("RGBA", "LA", "P") else im.convert("RGB")
        fn = base + "_" + name + ".png"
        im.save(os.path.join(out_dir, fn), optimize=True)
        images.append({"uri": fn}); textures.append({"sampler": 0, "source": len(images) - 1})
        return len(textures) - 1
    diff_t = add_tex(a.diffuse, "diffuse", True)
    mat = {"name": a.name or base, "pbrMetallicRoughness": {"baseColorTexture": {"index": diff_t}, "metallicFactor": 0.0, "roughnessFactor": 0.85}, "doubleSided": True}
    if a.cutout: mat["alphaMode"] = "MASK"; mat["alphaCutoff"] = 0.5
    if a.normal and os.path.exists(a.normal):
        mat["normalTexture"] = {"index": add_tex(a.normal, "normal", False)}
    n_v = len(verts)
    gltf = {
        "asset": {"version": "2.0", "generator": "Vortex obj2gltf"},
        "scene": 0, "scenes": [{"nodes": [0]}],
        "nodes": [{"mesh": 0, "name": a.name or base}],
        "meshes": [{"name": a.name or base, "primitives": [{"attributes": {"POSITION": 0, "NORMAL": 1, "TEXCOORD_0": 2}, "indices": 3, "material": 0}]}],
        "materials": [mat], "images": images, "textures": textures, "samplers": samplers,
        "buffers": [{"uri": bin_name, "byteLength": len(blob)}],
        "bufferViews": [
            {"buffer": 0, "byteOffset": 0, "byteLength": len(pbuf), "target": 34962},
            {"buffer": 0, "byteOffset": len(pbuf), "byteLength": len(nbuf), "target": 34962},
            {"buffer": 0, "byteOffset": len(pbuf) + len(nbuf), "byteLength": len(tbuf), "target": 34962},
            {"buffer": 0, "byteOffset": len(pbuf) + len(nbuf) + len(tbuf), "byteLength": len(ibuf), "target": 34963},
        ],
        "accessors": [
            {"bufferView": 0, "componentType": 5126, "count": n_v, "type": "VEC3", "min": mins, "max": maxs},
            {"bufferView": 1, "componentType": 5126, "count": n_v, "type": "VEC3"},
            {"bufferView": 2, "componentType": 5126, "count": n_v, "type": "VEC2"},
            {"bufferView": 3, "componentType": 5125, "count": len(index), "type": "SCALAR"},
        ],
    }
    with open(a.out, "w", encoding="utf-8") as f: json.dump(gltf, f, indent=1)
    print(f"{a.out}: {n_v} vertices, {len(index)//3} triangles, bounds {[round(v,2) for v in mins]} .. {[round(v,2) for v in maxs]}")

if __name__ == "__main__":
    main()
