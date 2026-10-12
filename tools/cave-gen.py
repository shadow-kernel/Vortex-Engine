#!/usr/bin/env python3
"""cave-gen.py — a procedural cave tunnel as glTF, to be buried under a terrain behind a Hole-brush mouth: a tube along a
winding path with rocky noise on the walls, a flattened walkable floor as its own primitive (a ground material), a flared
irregular mouth that reaches a little outside the hill, optional side chambers and a closed far end. The faces look INWARDS
(the player stands inside), so the walls are counter-clockwise seen from the axis.

    cave-gen.py --rock Assets/Textures/rock_face_03/rock_face_03_diff_2k.jpg --floor Assets/Textures/brown_mud_dry/brown_mud_dry_diff_2k.jpg \
                --out Assets/Models/Caves/cave_tunnel_a/cave_tunnel_a.gltf [--length 48] [--radius 4.5] [--seed 3] [--chambers 1]

The texture sets are Poly Haven style (<stem>_diff / _nor_dx / _arm siblings are picked up and copied next to the model,
resized to --tex-size). Local frame: the mouth centre is at the origin with the floor at y = 0, the tunnel runs towards -z.
Needs Pillow for the textures only.
"""
import argparse, json, math, os, random, struct

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--rock", required=True, help="the wall texture set's diffuse (siblings _nor_dx / _arm are picked up)")
    ap.add_argument("--floor", help="the floor texture set's diffuse (default: the rock)")
    ap.add_argument("--out", required=True)
    ap.add_argument("--length", type=float, default=48.0, help="tunnel length along the path (m)")
    ap.add_argument("--radius", type=float, default=4.5, help="tube radius (m) — the height from floor to ceiling is about 1.3 × this")
    ap.add_argument("--mouth", type=float, default=1.35, help="the mouth's radius as a factor of --radius")
    ap.add_argument("--apron", type=float, default=2.0, help="how far the mouth collar reaches outside the hill (m)")
    ap.add_argument("--chambers", type=int, default=1, help="wider chambers along the way (0..3)")
    ap.add_argument("--wobble", type=float, default=0.55, help="how much the path winds sideways (0..1)")
    ap.add_argument("--descend", type=float, default=2.5, help="how far the floor drops from the mouth to the end (m)")
    ap.add_argument("--turn", type=float, default=0.0, help="how far the tunnel turns over its length (degrees, + = to the right walking in)")
    ap.add_argument("--rough", type=float, default=0.22, help="wall noise amplitude as a factor of the radius")
    ap.add_argument("--shell", type=float, default=0.2, help="the outer rock shell covers the mouth up to this t along the tunnel (0 = none)")
    ap.add_argument("--shell-offset", type=float, default=1.2, help="how thick the mouth's rock lip is (m)")
    ap.add_argument("--rings", type=int, default=40)
    ap.add_argument("--segs", type=int, default=28)
    ap.add_argument("--tile", type=float, default=3.0, help="metres per texture repeat")
    ap.add_argument("--rock-tint", type=float, nargs=3, default=[0.74, 0.66, 0.6], help="the wall material's colour factor (a pale sandstone reads as rock when it is darker than the sand around it)")
    ap.add_argument("--floor-tint", type=float, nargs=3, default=[0.9, 0.88, 0.84])
    ap.add_argument("--tex-size", type=int, default=2048)
    ap.add_argument("--seed", type=int, default=3)
    ap.add_argument("--name", default=None)
    ap.add_argument("--points", help="also write the walkable floor points (local, JSON) here — for planting the cave")
    a = ap.parse_args()
    rnd = random.Random(a.seed)

    # ---------------------------------------------------------------- value noise (smooth, periodic around the tube)
    grad = [[rnd.uniform(-1, 1) for _ in range(64)] for _ in range(64)]
    def vnoise(u, v):
        """smooth 2-d value noise, u periodic in 64 (around the tube), v along it"""
        iu, iv = int(math.floor(u)), int(math.floor(v)); fu, fv = u - iu, v - iv
        fu = fu * fu * (3 - 2 * fu); fv = fv * fv * (3 - 2 * fv)
        def g(i, j): return grad[i % 64][j % 64]
        return (g(iu, iv) * (1 - fu) + g(iu + 1, iv) * fu) * (1 - fv) + (g(iu, iv + 1) * (1 - fu) + g(iu + 1, iv + 1) * fu) * fv
    def fbm(u, v):
        return vnoise(u, v) + 0.5 * vnoise(u * 2.03 + 7, v * 2.03 + 3) + 0.25 * vnoise(u * 4.1 + 2, v * 4.1 + 9)

    # ---------------------------------------------------------------- the path: from the mouth (z = 0) into the hill (-z)
    L = a.length
    bends = [(rnd.uniform(0.25, 0.75), rnd.uniform(-1, 1)) for _ in range(2)]
    turn = math.radians(a.turn)
    def heading(t):
        """the axis direction at t, in radians from -z towards +x (to the right walking in)"""
        u = min(1.0, max(0.0, t)); return turn * (u * u * (3 - 2 * u))
    # the axis' ground track: the heading integrated along the length, then straight on beyond both ends
    STEPS = 400
    track = [(0.0, 0.0)]
    for i in range(STEPS):
        h = heading((i + 0.5) / STEPS)
        px, pz = track[-1]
        track.append((px + math.sin(h) * L / STEPS, pz - math.cos(h) * L / STEPS))
    def ground(t):
        if t <= 0.0: return (0.0, -L * t)
        if t >= 1.0:
            ex, ez = track[-1]; d = (t - 1.0) * L
            return (ex + math.sin(turn) * d, ez - math.cos(turn) * d)
        f = t * STEPS; i = min(STEPS - 1, int(f)); w = f - i
        (x0, z0), (x1, z1) = track[i], track[i + 1]
        return (x0 + (x1 - x0) * w, z0 + (z1 - z0) * w)
    def centre(t):
        """the tube axis at t (0 = mouth, 1 = far end; beyond both ends it runs straight on); y is the axis height above
        the mouth floor"""
        u = min(1.0, max(0.0, t))
        side = 0.0
        for (tc, amp) in bends:
            side += a.wobble * L * 0.18 * amp * math.sin(math.pi * min(1.0, max(0.0, (u - tc + 0.35) / 0.7))) ** 2
        side += a.wobble * L * 0.03 * (math.sin(u * 11.0 + a.seed) - math.sin(a.seed))   # no sideways offset at the mouth
        gx, gz = ground(t)
        h = heading(t)
        y = -a.descend * (u * u * (3 - 2 * u)) + 0.4 * (math.sin(u * 7.0 + 1.0) - math.sin(1.0))
        return (gx + math.cos(h) * side, y, gz + math.sin(h) * side)
    chambers = []
    for c in range(max(0, min(3, a.chambers))):
        chambers.append((rnd.uniform(0.35, 0.8), rnd.uniform(1.5, 1.9), rnd.uniform(0.08, 0.14)))   # position, size factor, width
    def radius_at(t):
        r = a.radius * (1.0 + 0.12 * math.sin(t * 9.0 + 2.0))
        for (tc, f, w) in chambers:
            r *= 1.0 + (f - 1.0) * math.exp(-((t - tc) / w) ** 2)
        if t < 0.12: r *= 1.0 + (a.mouth - 1.0) * (1.0 - t / 0.12) ** 1.5            # the flared mouth
        r *= 1.0 - 0.45 * max(0.0, (min(t, 1.0) - 0.94) / 0.06) ** 1.2                 # the far end narrows before the cap
        if t > 1.0: r *= max(0.04, 1.0 - (t - 1.0) / 0.06)                               # … and closes down to a point over the last rings (a tiny cap fan, no stretched texture)
        return r

    P, N, T = [], [], []
    def vertex(p, n, uv): P.append(p); N.append(n); T.append(uv); return len(P) - 1
    prims = {"walls": [], "floor": []}

    # the rings: ring 0 sits --apron metres OUTSIDE the mouth (the collar that hides the terrain hole's edge), the last at -L
    rings = a.rings
    ts = [-(a.apron / L) * (1.0 - r / 3.0) for r in range(3)] + [r / (rings - 1) for r in range(rings)] + [1.02, 1.04, 1.055]
    ts = sorted(set(ts))
    ring_rows = []          # per ring: list of (angle, vertex index) for the wall ring (segs + 1, closed)
    ring_centres = []       # per ring: the axis point
    floor_rows = []         # per ring: the floor strip's vertex indices (across)
    floor_pts = []          # walkable floor points for planting (local)
    arc = 0.0
    segs = a.segs
    prev_c = None
    for ri, t in enumerate(ts):
        tc = max(0.0, min(1.0, t))
        c = centre(t)
        # the tube frame: forward along the path (a symmetric difference — the path runs on straight beyond its ends, so the
        # closing rings and the collar keep a proper frame), right / up around it
        ca_, cb_ = centre(t - 0.01), centre(t + 0.01)
        f = (cb_[0] - ca_[0], 0.0, cb_[2] - ca_[2])
        fl = math.hypot(f[0], f[2]) or 1.0; f = (f[0] / fl, 0.0, f[2] / fl)
        right = (-f[2], 0.0, f[0]); up = (0.0, 1.0, 0.0)
        r = radius_at(t if t > 1.0 else tc)
        if t < 0: r = radius_at(0.0) * (1.0 + 0.35 * (-t * L / max(0.1, a.apron)))    # the collar opens further outside
        floor_y = c[1] - r * 0.62 if t >= 0 else centre(0)[1] - radius_at(0.0) * 0.62
        floor_y -= 0.0 if t >= 0 else 0.25 * (-t * L / max(0.1, a.apron))              # the apron floor dips to meet the ground
        if prev_c is not None: arc += math.dist(prev_c, c)
        prev_c = c
        ring_centres.append(c)
        v_tex = arc / a.tile
        row = []
        floor_x = []
        for s in range(segs + 1):
            ang = 2 * math.pi * s / segs                      # 0 = right, pi/2 = up (ceiling), pi = left, 3pi/2 = floor
            # rocky bulges: the noise is read on a circle around the tube (cos / sin of the angle), so the ring closes on
            # itself — reading it along the angle left the first and the last vertex at different radii: a crack along
            # the whole tunnel that showed the sky
            nz = fbm(4.0 + math.cos(ang) * 1.8 + v_tex * 0.55, 4.0 + math.sin(ang) * 1.8 + v_tex * 1.3 + ri * 0.05)
            rr = r * (1.0 + a.rough * nz) * (1.0 - 0.18 * max(0.0, -math.sin(ang)))   # a little flatter at the bottom
            px = c[0] + right[0] * math.cos(ang) * rr + up[0] * math.sin(ang) * rr
            py = c[1] + right[1] * math.cos(ang) * rr + up[1] * math.sin(ang) * rr
            pz = c[2] + right[2] * math.cos(ang) * rr + up[2] * math.sin(ang) * rr
            below = floor_y - py
            if below > 0:
                # under the floor plane: the wall vertex drops onto the floor so the strip closes against it
                py = floor_y + 0.02 * nz
            # inward normal: towards the axis (refined later from the faces)
            n = (-(math.cos(ang) * right[0] + math.sin(ang) * up[0]), -(math.cos(ang) * right[1] + math.sin(ang) * up[1]), -(math.cos(ang) * right[2] + math.sin(ang) * up[2]))
            row.append(vertex((px, py, pz), n, (s / segs * (2 * math.pi * a.radius / a.tile), v_tex)))
        ring_rows.append(row)
        # the floor strip: a flat band across the tube at floor_y, from the left wall foot to the right wall foot
        half = r * 0.92
        n_across = 7
        fr = []
        for k in range(n_across):
            u = -1.0 + 2.0 * k / (n_across - 1)
            dx = right[0] * u * half; dz = right[2] * u * half
            bump = (0.04 + 0.04 * max(-1.0, min(1.0, fbm(u * 3.0 + 20, v_tex * 1.1 + 5)))) if t >= 0 else 0.03   # the strip sits a few cm above the walls' feet (no z-fighting)
            fr.append(vertex((c[0] + dx, floor_y + bump, c[2] + dz), (0.0, 1.0, 0.0), ((c[0] + dx) / a.tile, (c[2] + dz) / a.tile)))
            if 0.02 <= t <= 0.97 and abs(u) < 0.8: floor_pts.append([round(c[0] + dx, 2), round(floor_y + bump, 2), round(c[2] + dz, 2)])
        floor_rows.append(fr)

    # ---------------------------------------------------------------- the faces (counter-clockwise seen from INSIDE)
    def tri(prim, i0, i1, i2):
        p0, p1, p2 = P[i0], P[i1], P[i2]
        e1 = (p1[0] - p0[0], p1[1] - p0[1], p1[2] - p0[2]); e2 = (p2[0] - p0[0], p2[1] - p0[1], p2[2] - p0[2])
        n = (e1[1] * e2[2] - e1[2] * e2[1], e1[2] * e2[0] - e1[0] * e2[2], e1[0] * e2[1] - e1[1] * e2[0])
        prims[prim] += [i0, i1, i2]
        return n
    for ri in range(len(ring_rows) - 1):
        A, B = ring_rows[ri], ring_rows[ri + 1]
        for s in range(segs):
            # orientation: the face normal must point towards the tube axis between the two rings
            i0, i1, i2, i3 = A[s], A[s + 1], B[s + 1], B[s]
            ca = [(P[i0][k] + P[i1][k] + P[i2][k] + P[i3][k]) / 4 for k in range(3)]
            cc = [(ring_centres[ri][k] + ring_centres[ri + 1][k]) * 0.5 for k in range(3)]
            to_axis = (cc[0] - ca[0], cc[1] - ca[1], cc[2] - ca[2])
            e1 = (P[i2][0] - P[i0][0], P[i2][1] - P[i0][1], P[i2][2] - P[i0][2]); e2 = (P[i1][0] - P[i0][0], P[i1][1] - P[i0][1], P[i1][2] - P[i0][2])
            n = (e1[1] * e2[2] - e1[2] * e2[1], e1[2] * e2[0] - e1[0] * e2[2], e1[0] * e2[1] - e1[1] * e2[0])
            if n[0] * to_axis[0] + n[1] * to_axis[1] + n[2] * to_axis[2] >= 0: tri("walls", i0, i2, i1); tri("walls", i0, i3, i2)
            else: tri("walls", i0, i1, i2); tri("walls", i0, i2, i3)
        # the floor band between the rings (normal +y)
        FA, FB = floor_rows[ri], floor_rows[ri + 1]
        for k in range(len(FA) - 1):
            i0, i1, i2, i3 = FA[k], FA[k + 1], FB[k + 1], FB[k]
            e1 = (P[i2][0] - P[i0][0], P[i2][1] - P[i0][1], P[i2][2] - P[i0][2]); e2 = (P[i1][0] - P[i0][0], P[i1][1] - P[i0][1], P[i1][2] - P[i0][2])
            ny = e1[2] * e2[0] - e1[0] * e2[2]
            if ny >= 0: tri("floor", i0, i2, i1); tri("floor", i0, i3, i2)
            else: tri("floor", i0, i1, i2); tri("floor", i0, i2, i3)
    # the mouth's OUTER shell: the collar and the flare again, pushed outwards and facing out, so the entrance reads as a
    # rock arch bulging out of the hillside from outside too (back faces are culled, one shell alone looks hollow). Its
    # feet drop into the ground as a skirt.
    shell_rows = []
    for ri, t in enumerate(ts):
        if t > a.shell: break
        row = ring_rows[ri]
        srow = []
        for s in range(segs + 1):
            i = row[s]
            p, n = P[i], N[i]
            # push away from the axis (the inward normal points at it); the feet also go down 3 m
            ang = 2 * math.pi * s / segs
            sa = math.sin(ang)
            # thicker over the top (the outcrop above the opening), and the lower third sinks into the ground smoothly
            off = a.shell_offset * (1.0 + 0.8 * max(0.0, sa))
            sink = 2.0 * min(1.0, max(0.0, (-sa - 0.1) / 0.5)) ** 2
            q = (p[0] - n[0] * off, p[1] - n[1] * off * 0.6 - sink, p[2] - n[2] * off)
            srow.append(vertex(q, (-n[0], -n[1], -n[2]), (T[i][0] * 1.1, T[i][1] * 1.1 + 0.37)))
        shell_rows.append(srow)
    for ri in range(len(shell_rows) - 1):
        A, B = shell_rows[ri], shell_rows[ri + 1]
        for s in range(segs):
            i0, i1, i2, i3 = A[s], A[s + 1], B[s + 1], B[s]
            ca = [(P[i0][k] + P[i1][k] + P[i2][k] + P[i3][k]) / 4 for k in range(3)]
            cc = [(ring_centres[ri][k] + ring_centres[ri + 1][k]) * 0.5 for k in range(3)]
            to_axis = (cc[0] - ca[0], cc[1] - ca[1], cc[2] - ca[2])
            e1 = (P[i2][0] - P[i0][0], P[i2][1] - P[i0][1], P[i2][2] - P[i0][2]); e2 = (P[i1][0] - P[i0][0], P[i1][1] - P[i0][1], P[i1][2] - P[i0][2])
            n = (e1[1] * e2[2] - e1[2] * e2[1], e1[2] * e2[0] - e1[0] * e2[2], e1[0] * e2[1] - e1[1] * e2[0])
            # outward: the face normal points AWAY from the axis
            if n[0] * to_axis[0] + n[1] * to_axis[1] + n[2] * to_axis[2] <= 0: tri("walls", i0, i2, i1); tri("walls", i0, i3, i2)
            else: tri("walls", i0, i1, i2); tri("walls", i0, i2, i3)
    # the shell's front rim closes onto the inner collar's first ring (a thick lip around the opening)
    if shell_rows:
        A, B = shell_rows[0], ring_rows[0]
        for s in range(segs):
            i0, i1, i2, i3 = A[s], A[s + 1], B[s + 1], B[s]
            e1 = (P[i2][0] - P[i0][0], P[i2][1] - P[i0][1], P[i2][2] - P[i0][2]); e2 = (P[i1][0] - P[i0][0], P[i1][1] - P[i0][1], P[i1][2] - P[i0][2])
            nz = e1[0] * e2[1] - e1[1] * e2[0]
            # the lip faces +z (out of the mouth)
            if nz >= 0: tri("walls", i0, i2, i1); tri("walls", i0, i3, i2)
            else: tri("walls", i0, i1, i2); tri("walls", i0, i2, i3)

    # the far end: a cap fan to a centre point (a rock wall), facing the mouth
    last = ring_rows[-1]; cl = centre(ts[-1])
    cap = vertex((cl[0], cl[1] - 0.1, cl[2] - 0.3), (0.0, 0.0, 1.0), (0.5, arc / a.tile))
    for s in range(segs):
        i0, i1 = last[s], last[s + 1]
        e1 = (P[i1][0] - P[cap][0], P[i1][1] - P[cap][1], P[i1][2] - P[cap][2]); e2 = (P[i0][0] - P[cap][0], P[i0][1] - P[cap][1], P[i0][2] - P[cap][2])
        nz = e1[0] * e2[1] - e1[1] * e2[0]
        if nz >= 0: tri("walls", cap, i1, i0)
        else: tri("walls", cap, i0, i1)
    # the floor's far end closes with the cap too (a wedge from the last floor row to the cap point, in the floor's uv space)
    FL = floor_rows[-1]
    fcap = vertex(P[cap], (0.0, 1.0, 0.0), (P[cap][0] / a.tile, P[cap][2] / a.tile))
    for k in range(len(FL) - 1):
        i0, i1 = FL[k], FL[k + 1]
        e1 = (P[i1][0] - P[fcap][0], P[i1][1] - P[fcap][1], P[i1][2] - P[fcap][2]); e2 = (P[i0][0] - P[fcap][0], P[i0][1] - P[fcap][1], P[i0][2] - P[fcap][2])
        ny = e1[2] * e2[0] - e1[0] * e2[2]
        if ny >= 0: tri("floor", fcap, i0, i1)
        else: tri("floor", fcap, i1, i0)

    # ---------------------------------------------------------------- smooth normals from the faces (area weighted)
    acc = [[0.0, 0.0, 0.0] for _ in P]
    for key in prims:
        idx = prims[key]
        for i in range(0, len(idx), 3):
            i0, i1, i2 = idx[i], idx[i + 1], idx[i + 2]
            p0, p1, p2 = P[i0], P[i1], P[i2]
            e1 = (p1[0] - p0[0], p1[1] - p0[1], p1[2] - p0[2]); e2 = (p2[0] - p0[0], p2[1] - p0[1], p2[2] - p0[2])
            n = (e1[1] * e2[2] - e1[2] * e2[1], e1[2] * e2[0] - e1[0] * e2[2], e1[0] * e2[1] - e1[1] * e2[0])
            for i in (i0, i1, i2):
                for k in range(3): acc[i][k] += n[k]
    for i in range(len(P)):
        l = math.sqrt(sum(v * v for v in acc[i]))
        N[i] = tuple(v / l for v in acc[i]) if l > 1e-9 else N[i]
    # the wall ring's seam (s = 0 and s = segs share a position): average their normals so the seam does not show
    for row in ring_rows:
        n0, n1 = N[row[0]], N[row[-1]]
        m = tuple((n0[k] + n1[k]) * 0.5 for k in range(3)); l = math.sqrt(sum(v * v for v in m)) or 1.0
        N[row[0]] = N[row[-1]] = tuple(v / l for v in m)

    # ---------------------------------------------------------------- glTF
    out_dir = os.path.dirname(os.path.abspath(a.out)); os.makedirs(out_dir, exist_ok=True)
    base = os.path.splitext(os.path.basename(a.out))[0]
    name = a.name or base
    pbuf, nbuf, tbuf = bytearray(), bytearray(), bytearray()
    mins = [1e30] * 3; maxs = [-1e30] * 3
    for p, nn, uv in zip(P, N, T):
        for c in range(3): mins[c] = min(mins[c], p[c]); maxs[c] = max(maxs[c], p[c])
        pbuf += struct.pack("<3f", *p); nbuf += struct.pack("<3f", *nn); tbuf += struct.pack("<2f", *uv)
    ibufs = {}
    for key in ("walls", "floor"):
        b = bytearray()
        for i in prims[key]: b += struct.pack("<I", i)
        ibufs[key] = bytes(b)
    blob = bytes(pbuf) + bytes(nbuf) + bytes(tbuf) + ibufs["walls"] + ibufs["floor"]
    with open(os.path.join(out_dir, base + ".bin"), "wb") as f: f.write(blob)

    from PIL import Image
    images = []
    def texture_set(diffuse, tag):
        """copy a Poly Haven style set (<stem>_diff / _nor_dx / _arm, any resolution suffix) next to the model as JPEG"""
        stem = diffuse
        for suf in ("_diff_4k", "_diff_2k", "_diff_1k", "_diff", "_diffuse"):
            for ext in (".jpg", ".png"):
                if stem.endswith(suf + ext): stem = stem[: -len(suf) - len(ext)]; break
        def sibling(kind):
            for ext in (".jpg", ".png"):
                for res in ("_4k", "_2k", "_1k", ""):
                    c = stem + "_" + kind + res + ext
                    if os.path.exists(c): return c
            return None
        out = {}
        for kind, mode in (("diff", "RGB"), ("nor_dx", "RGB"), ("arm", "RGB")):
            src = diffuse if kind == "diff" else sibling(kind)
            if not src or not os.path.exists(src): continue
            im = Image.open(src).convert(mode)
            if max(im.size) > a.tex_size:
                fct = a.tex_size / max(im.size); im = im.resize((max(1, round(im.size[0] * fct)), max(1, round(im.size[1] * fct))), Image.LANCZOS)
            fn = base + "_" + tag + "_" + kind + ".jpg"
            im.save(os.path.join(out_dir, fn), quality=88, optimize=True)
            out[kind] = len(images); images.append({"uri": fn})
        return out
    rock = texture_set(a.rock, "rock")
    floor = texture_set(a.floor, "floor") if a.floor and os.path.abspath(a.floor) != os.path.abspath(a.rock) else rock
    textures = [{"sampler": 0, "source": i} for i in range(len(images))]
    def material(nm, tex, tint=(1.0, 1.0, 1.0)):
        m = {"name": nm, "pbrMetallicRoughness": {"baseColorFactor": [tint[0], tint[1], tint[2], 1.0], "metallicFactor": 0.0, "roughnessFactor": 1.0}, "doubleSided": False}
        if "diff" in tex: m["pbrMetallicRoughness"]["baseColorTexture"] = {"index": tex["diff"]}
        if "nor_dx" in tex: m["normalTexture"] = {"index": tex["nor_dx"]}
        if "arm" in tex:
            m["occlusionTexture"] = {"index": tex["arm"]}
            m["pbrMetallicRoughness"]["metallicRoughnessTexture"] = {"index": tex["arm"]}
        return m
    materials = [material(name + "_rock", rock, tuple(a.rock_tint)), material(name + "_floor", floor, tuple(a.floor_tint))]
    off_i = len(pbuf) + len(nbuf) + len(tbuf)
    views = [
        {"buffer": 0, "byteOffset": 0, "byteLength": len(pbuf), "target": 34962},
        {"buffer": 0, "byteOffset": len(pbuf), "byteLength": len(nbuf), "target": 34962},
        {"buffer": 0, "byteOffset": len(pbuf) + len(nbuf), "byteLength": len(tbuf), "target": 34962},
        {"buffer": 0, "byteOffset": off_i, "byteLength": len(ibufs["walls"]), "target": 34963},
        {"buffer": 0, "byteOffset": off_i + len(ibufs["walls"]), "byteLength": len(ibufs["floor"]), "target": 34963},
    ]
    accessors = [
        {"bufferView": 0, "componentType": 5126, "count": len(P), "type": "VEC3", "min": mins, "max": maxs},
        {"bufferView": 1, "componentType": 5126, "count": len(P), "type": "VEC3"},
        {"bufferView": 2, "componentType": 5126, "count": len(P), "type": "VEC2"},
        {"bufferView": 3, "componentType": 5125, "count": len(prims["walls"]), "type": "SCALAR"},
        {"bufferView": 4, "componentType": 5125, "count": len(prims["floor"]), "type": "SCALAR"},
    ]
    primitives = [{"attributes": {"POSITION": 0, "NORMAL": 1, "TEXCOORD_0": 2}, "indices": 3 + i, "material": i} for i in range(2)]
    gltf = {
        "asset": {"version": "2.0", "generator": "Vortex cave-gen"},
        "scene": 0, "scenes": [{"nodes": [0]}], "nodes": [{"mesh": 0, "name": name}],
        "meshes": [{"name": name, "primitives": primitives}],
        "materials": materials, "images": images, "textures": textures,
        "samplers": [{"magFilter": 9729, "minFilter": 9987, "wrapS": 10497, "wrapT": 10497}],
        "buffers": [{"uri": base + ".bin", "byteLength": len(blob)}],
        "bufferViews": views, "accessors": accessors,
    }
    with open(a.out, "w", encoding="utf-8") as f: json.dump(gltf, f, indent=1)
    if a.points:
        path = [[round(ring_centres[i][0], 2), round(ring_centres[i][1], 2), round(ring_centres[i][2], 2), round(radius_at(ts[i]), 2), round(ts[i], 3)] for i in range(len(ts))]
        with open(a.points, "w", encoding="utf-8") as f: json.dump({"floor": floor_pts, "mouth": [centre(0)[0], 0.0, 0.0], "end": list(centre(1.0)), "path": path}, f)
    tris = sum(len(v) for v in prims.values()) // 3
    mouth_floor = min(P[i][1] for i in floor_rows[[i for i, t in enumerate(ts) if t >= 0][0]])
    print(f"mouth floor at local y {mouth_floor:.2f}: place the entity {-mouth_floor:.2f} m above the ground in front of the mouth")
    print(f"{a.out}: {len(P)} vertices, {tris} triangles, bounds {[round(v, 1) for v in mins]}..{[round(v, 1) for v in maxs]}, {len(floor_pts)} floor points")

if __name__ == "__main__":
    main()
