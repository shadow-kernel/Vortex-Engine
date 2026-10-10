#!/usr/bin/env python3
"""gltf-decimate.py — cut a glTF 2.0 model (separate .bin, the way Blender / Poly Haven export it) down to a target triangle
count while keeping its UVs, materials and textures: every primitive is decimated on its own with MeshLab's quadric edge
collapse WITH texture-seam preservation (pymeshlab), vertex normals are recomputed, and a new .gltf + .bin pair is written.
Photoscanned Poly Haven plants and rocks come at 0.1 – 2 M triangles; instanced as foliage they need 2 – 40 k.

    gltf-decimate.py in.gltf out.gltf --target 20000 [--min-per-primitive 300] [--quality 0.3] [--boundary]

needs: pip install pymeshlab numpy
"""
import argparse, json, os, struct, sys
import numpy as np
import pymeshlab as ml

CT_SIZE = {5120: 1, 5121: 1, 5122: 2, 5123: 2, 5125: 4, 5126: 4}
CT_NP = {5120: np.int8, 5121: np.uint8, 5122: np.int16, 5123: np.uint16, 5125: np.uint32, 5126: np.float32}
T_N = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}


def read_accessor(g, bins, idx):
    a = g["accessors"][idx]
    bv = g["bufferViews"][a["bufferView"]]
    n = T_N[a["type"]]; dt = CT_NP[a["componentType"]]; size = CT_SIZE[a["componentType"]] * n
    base = bins[bv["buffer"]]
    off = bv.get("byteOffset", 0) + a.get("byteOffset", 0)
    stride = bv.get("byteStride", size)
    if stride == size:
        arr = np.frombuffer(base, dtype=dt, count=a["count"] * n, offset=off).reshape(a["count"], n)
    else:
        arr = np.stack([np.frombuffer(base, dtype=dt, count=n, offset=off + i * stride) for i in range(a["count"])])
    return np.array(arr)


def decimate(V, F, UV, target, quality, boundary):
    """Return (V', F', UV') with about `target` faces; UV may be None."""
    ms = ml.MeshSet()
    if UV is not None:
        ms.add_mesh(ml.Mesh(vertex_matrix=V.astype(np.float64), face_matrix=F.astype(np.int32), v_tex_coords_matrix=UV.astype(np.float64)))
        ms.apply_filter("compute_texcoord_transfer_vertex_to_wedge")
        ms.apply_filter("meshing_decimation_quadric_edge_collapse_with_texture", targetfacenum=int(target), qualitythr=float(quality),
                        extratcoordw=1.0, preserveboundary=bool(boundary), optimalplacement=True, planarquadric=False)
        m = ms.current_mesh()
        F2 = m.face_matrix(); V2 = m.vertex_matrix(); W = m.wedge_tex_coord_matrix().reshape(-1, 3, 2)
        # split vertices per distinct (vertex, uv) wedge so the glTF stays per-vertex
        keys = {}; verts = []; uvs = []; faces = np.zeros_like(F2)
        for fi in range(F2.shape[0]):
            for c in range(3):
                vi = int(F2[fi, c]); u, v = float(W[fi, c, 0]), float(W[fi, c, 1])
                k = (vi, round(u, 6), round(v, 6))
                j = keys.get(k)
                if j is None:
                    j = len(verts); keys[k] = j; verts.append(V2[vi]); uvs.append((u, v))
                faces[fi, c] = j
        V3 = np.array(verts, dtype=np.float32); UV3 = np.array(uvs, dtype=np.float32)
        ms2 = ml.MeshSet(); ms2.add_mesh(ml.Mesh(vertex_matrix=V3.astype(np.float64), face_matrix=faces.astype(np.int32)))
        ms2.apply_filter("compute_normal_per_vertex")   # the split copies share a position, the normals stay smooth where the mesh is
        N3 = ms2.current_mesh().vertex_normal_matrix().astype(np.float32)
        return V3, faces.astype(np.uint32), UV3, N3
    ms.add_mesh(ml.Mesh(vertex_matrix=V.astype(np.float64), face_matrix=F.astype(np.int32)))
    ms.apply_filter("meshing_decimation_quadric_edge_collapse", targetfacenum=int(target), qualitythr=float(quality),
                    preserveboundary=bool(boundary), optimalplacement=True, planarquadric=False)
    ms.apply_filter("compute_normal_per_vertex")
    m = ms.current_mesh()
    return m.vertex_matrix().astype(np.float32), m.face_matrix().astype(np.uint32), None, m.vertex_normal_matrix().astype(np.float32)


def smooth_normals(V, F, UV, N):
    """Average the normals of split copies of the same position (UV seams stay geometrically smooth)."""
    key = np.round(V, 5)
    _, inv = np.unique(key, axis=0, return_inverse=True)
    acc = np.zeros((inv.max() + 1, 3), dtype=np.float64)
    np.add.at(acc, inv.ravel(), N)
    n = acc[inv.ravel()]
    l = np.linalg.norm(n, axis=1); l[l < 1e-9] = 1
    return (n / l[:, None]).astype(np.float32)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("input"); ap.add_argument("output")
    ap.add_argument("--target", type=int, required=True, help="triangles for the whole model")
    ap.add_argument("--min-per-primitive", type=int, default=300)
    ap.add_argument("--quality", type=float, default=0.3, help="MeshLab quality threshold (0.3 keeps triangles well shaped)")
    ap.add_argument("--boundary", action="store_true", help="preserve open boundaries (leaf cards, cut models)")
    args = ap.parse_args()

    src_dir = os.path.dirname(os.path.abspath(args.input))
    g = json.load(open(args.input))
    if g.get("skins") or g.get("animations"):
        sys.exit("skinned / animated models are not supported")
    bins = []
    for b in g["buffers"]:
        if "uri" not in b or b["uri"].startswith("data:"): sys.exit("embedded buffers are not supported")
        bins.append(open(os.path.join(src_dir, b["uri"]), "rb").read())

    prims = [(mi, pi, p) for mi, m in enumerate(g["meshes"]) for pi, p in enumerate(m["primitives"])]
    counts = []
    for _, _, p in prims:
        counts.append(g["accessors"][p["indices"]]["count"] // 3 if "indices" in p else g["accessors"][p["attributes"]["POSITION"]]["count"] // 3)
    total = sum(counts)
    blob = bytearray(); views = []; accessors = []

    def add(arr, target_kind, comp, typ, minmax=False):
        data = np.ascontiguousarray(arr).tobytes()
        while len(blob) % 4: blob.append(0)
        views.append({"buffer": 0, "byteOffset": len(blob), "byteLength": len(data), "target": target_kind})
        blob.extend(data)
        acc = {"bufferView": len(views) - 1, "componentType": comp, "count": int(arr.shape[0]), "type": typ}
        if minmax:
            acc["min"] = [float(x) for x in arr.min(axis=0)]; acc["max"] = [float(x) for x in arr.max(axis=0)]
        accessors.append(acc)
        return len(accessors) - 1

    out_tris = 0
    for (mi, pi, p), tris in zip(prims, counts):
        V = read_accessor(g, bins, p["attributes"]["POSITION"]).astype(np.float32)
        UV = read_accessor(g, bins, p["attributes"]["TEXCOORD_0"]).astype(np.float32) if "TEXCOORD_0" in p["attributes"] else None
        if "indices" in p: F = read_accessor(g, bins, p["indices"]).reshape(-1, 3).astype(np.int64)
        else: F = np.arange(V.shape[0], dtype=np.int64).reshape(-1, 3)
        target = max(args.min_per_primitive, int(round(args.target * tris / max(1, total))))
        if tris > target:
            V2, F2, UV2, N2 = decimate(V, F, UV, target, args.quality, args.boundary)
            if UV2 is not None: N2 = smooth_normals(V2, F2, UV2, N2)
        else:
            V2, F2, UV2 = V, F.astype(np.uint32), UV
            N2 = read_accessor(g, bins, p["attributes"]["NORMAL"]).astype(np.float32) if "NORMAL" in p["attributes"] else None
            if N2 is None:
                ms = ml.MeshSet(); ms.add_mesh(ml.Mesh(vertex_matrix=V2.astype(np.float64), face_matrix=F2.astype(np.int32))); ms.apply_filter("compute_normal_per_vertex")
                N2 = ms.current_mesh().vertex_normal_matrix().astype(np.float32)
        attrs = {"POSITION": add(V2, 34962, 5126, "VEC3", True), "NORMAL": add(N2, 34962, 5126, "VEC3")}
        if UV2 is not None: attrs["TEXCOORD_0"] = add(UV2, 34962, 5126, "VEC2")
        idx = add(F2.reshape(-1).astype(np.uint32), 34963, 5125, "SCALAR")
        newp = {"attributes": attrs, "indices": idx}
        if "material" in p: newp["material"] = p["material"]
        if "mode" in p: newp["mode"] = p["mode"]
        g["meshes"][mi]["primitives"][pi] = newp
        out_tris += F2.shape[0]
        print(f"  mesh {mi} primitive {pi}: {tris} -> {F2.shape[0]} triangles, {V2.shape[0]} vertices")

    out_dir = os.path.dirname(os.path.abspath(args.output)); os.makedirs(out_dir, exist_ok=True)
    bin_name = os.path.splitext(os.path.basename(args.output))[0] + ".bin"
    open(os.path.join(out_dir, bin_name), "wb").write(bytes(blob))
    g["buffers"] = [{"byteLength": len(blob), "uri": bin_name}]
    g["bufferViews"] = views; g["accessors"] = accessors
    # textures keep their relative uris when the output sits next to the input; otherwise rewrite them relative to the output
    for im in g.get("images", []):
        if "uri" in im and not im["uri"].startswith("data:"):
            im["uri"] = os.path.relpath(os.path.join(src_dir, im["uri"]), out_dir).replace(os.sep, "/")
    g.setdefault("asset", {})["generator"] = "Vortex gltf-decimate.py (pymeshlab) after " + g.get("asset", {}).get("generator", "?")
    json.dump(g, open(args.output, "w"), separators=(",", ":"))
    print(f"{args.input}: {total} -> {out_tris} triangles -> {args.output}")


if __name__ == "__main__":
    main()
