using System;
using Editor.Utilities;

namespace Editor.DllWrapper
{
    /// <summary>Audio gizmos (issue #18): camera-facing speaker/listener icons at every audio entity, fine
    /// WIREFRAME-net min/max distance spheres for the selected AudioSource and the reverb-zone boundary
    /// (+falloff shell) for a selected ReverbZone. Shapes go through the gizmo WIRE pass (one draw per whole
    /// sphere — thin GPU wireframe lines, not fat tube segments), all materials are UNLIT so the colors stay
    /// bright and readable from every angle. Submitted every edit-mode frame, so inspector edits and entity
    /// drags update live. The zone is drawn in its entity's world matrix (rotated + scaled), exactly as the
    /// runtime weight test (AudioPlaybackService.ZoneWeight) measures it (#497).</summary>
    public static partial class VortexAPI
    {
        private static bool _audioGizmosInitialized;
        private static long _audioIconMaterial = ID.INVALID_ID;      // speaker, matches the component's #CE9178 accent
        private static long _audioIconSelMaterial = ID.INVALID_ID;   // speaker while selected (bright amber)
        private static long _listenerIconMaterial = ID.INVALID_ID;   // listener head (green like the play accent)
        private static long _audioMinMaterial = ID.INVALID_ID;       // min-distance sphere (warm yellow)
        private static long _audioMaxMaterial = ID.INVALID_ID;       // max-distance sphere (orange)
        private static long _reverbZoneMaterial = ID.INVALID_ID;     // zone boundary (cyan)
        private static long _reverbFalloffMaterial = ID.INVALID_ID;  // boundary + falloff shell (muted teal)

        private static long MakeUnlitMaterial(float r, float g, float b)
        {
            long mat = CreateMaterial();
            if (mat != ID.INVALID_ID)
            {
                SetMaterialColor(mat, r, g, b, 1.0f);
                // Unlit = constant color, never swallowed by scene lighting/shadow. Emissive stays at 1.0:
                // higher values push every color through the tone mapper toward washed-out white.
                SetMaterialAsUnlit(mat, true);
                SetMaterialEmissiveBrightness(mat, 1.0f);
            }
            return mat;
        }

        private static void EnsureAudioGizmoResources()
        {
            if (!_gizmosInitialized) InitializeGizmos();
            if (_audioGizmosInitialized || _gizmoCube == ID.INVALID_ID) return;

            // Highly saturated bases — the unlit path tone-maps + gamma-corrects, which desaturates,
            // so start punchier than the target on-screen tone.
            _audioIconMaterial = MakeUnlitMaterial(0.95f, 0.52f, 0.30f);
            _audioIconSelMaterial = MakeUnlitMaterial(1.0f, 0.72f, 0.15f);
            _listenerIconMaterial = MakeUnlitMaterial(0.15f, 0.95f, 0.45f);
            _audioMinMaterial = MakeUnlitMaterial(1.0f, 0.85f, 0.10f);
            _audioMaxMaterial = MakeUnlitMaterial(1.0f, 0.42f, 0.05f);
            _reverbZoneMaterial = MakeUnlitMaterial(0.08f, 0.72f, 1.0f);
            _reverbFalloffMaterial = MakeUnlitMaterial(0.06f, 0.32f, 0.45f);
            _audioGizmosInitialized = true;
        }

        private static void AudioEdge(float[] p1, float[] p2, long material, float thickness)
        {
            if (material == ID.INVALID_ID || _gizmoCube == ID.INVALID_ID) return;
            float dx = p2[0] - p1[0], dy = p2[1] - p1[1], dz = p2[2] - p1[2];
            float len = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 0.0005f) return;
            dx /= len; dy /= len; dz /= len;
            float mx = (p1[0] + p2[0]) * 0.5f, my = (p1[1] + p2[1]) * 0.5f, mz = (p1[2] + p2[2]) * 0.5f;
            SubmitGizmoForRendering(_gizmoCube, material, BuildEdgeMatrix(mx, my, mz, dx, dy, dz, len, thickness));
        }

        // One whole wireframe-net sphere in a single draw (_gizmoSphere is radius 0.5 -> scale = diameter).
        private static void AudioWireSphere(float cx, float cy, float cz, float rad, long material)
        {
            if (rad < 0.005f) return; // degenerate/negative (hand-edited scene files) — never draw mirrored phantoms
            if (_gizmoSphere == ID.INVALID_ID || material == ID.INVALID_ID) return;
            float d = rad * 2f;
            SubmitGizmoWireForRendering(_gizmoSphere, material, new float[]
            {
                d, 0, 0, 0,
                0, d, 0, 0,
                0, 0, d, 0,
                cx, cy, cz, 1
            });
        }

        // Rows are the images of the unit axes (same layout as BuildEdgeMatrix), last row translation.
        private static float[] BuildBasisMatrix(
            float px, float py, float pz,
            float xx, float xy, float xz, float sx,
            float yx, float yy, float yz, float sy,
            float zx, float zy, float zz, float sz)
        {
            return new float[]
            {
                xx * sx, xy * sx, xz * sx, 0,
                yx * sy, yy * sy, yz * sy, 0,
                zx * sz, zy * sz, zz * sz, 0,
                px, py, pz, 1
            };
        }

        /// <summary>Camera-facing speaker glyph (box body + horn + sound rays when selected) at an
        /// AudioSource entity's position. World-size like the camera icons, so it reads at a glance.</summary>
        public static void RenderAudioSourceIcon(float px, float py, float pz, float camX, float camY, float camZ, bool selected)
        {
            EnsureAudioGizmoResources();
            if (_gizmoCube == ID.INVALID_ID || _audioIconMaterial == ID.INVALID_ID) return;

            // Billboard basis: f toward the camera, r horizontal, u = f × r (right-handed, checked).
            float fx = camX - px, fy = camY - py, fz = camZ - pz;
            float fl = (float)Math.Sqrt(fx * fx + fy * fy + fz * fz);
            if (fl < 0.05f) return; // camera sits on the icon
            fx /= fl; fy /= fl; fz /= fl;
            float rx = fz, ry = 0f, rz = -fx;
            float rl = (float)Math.Sqrt(rx * rx + rz * rz);
            if (rl < 0.01f) { rx = 1f; rz = 0f; rl = 1f; } // looking straight down/up
            rx /= rl; rz /= rl;
            float ux = fy * rz - fz * ry, uy = fz * rx - fx * rz, uz = fx * ry - fy * rx;

            long mat = selected ? _audioIconSelMaterial : _audioIconMaterial;

            // Speaker body: flat box on the left half of the glyph.
            SubmitGizmoForRendering(_gizmoCube, mat, BuildBasisMatrix(
                px - rx * 0.07f, py - ry * 0.07f, pz - rz * 0.07f,
                rx, ry, rz, 0.10f,
                ux, uy, uz, 0.16f,
                fx, fy, fz, 0.05f));

            // Horn: cone apex (+Y of the mesh) points back into the box, base opens to the right.
            // Basis (X=u, Y=-r, Z=f) stays right-handed, so the cone is not rendered inside-out.
            SubmitGizmoForRendering(_gizmoCone, mat, BuildBasisMatrix(
                px + rx * 0.06f, py + ry * 0.06f, pz + rz * 0.06f,
                ux, uy, uz, 0.34f,
                -rx, -ry, -rz, 0.18f,
                fx, fy, fz, 0.34f));

            if (selected)
            {
                // Three sound rays fanning out of the horn.
                for (int i = -1; i <= 1; i++)
                {
                    double a = i * 0.45;
                    float dxr = (float)Math.Cos(a), dur = (float)Math.Sin(a);
                    float dx = rx * dxr + ux * dur, dy = ry * dxr + uy * dur, dz = rz * dxr + uz * dur;
                    AudioEdge(
                        new float[] { px + dx * 0.20f, py + dy * 0.20f, pz + dz * 0.20f },
                        new float[] { px + dx * 0.34f, py + dy * 0.34f, pz + dz * 0.34f },
                        mat, 0.02f);
                }
            }
        }

        /// <summary>Camera-facing listener glyph (head + ears) at an AudioListener entity's position.</summary>
        public static void RenderAudioListenerIcon(float px, float py, float pz, float camX, float camY, float camZ)
        {
            EnsureAudioGizmoResources();
            if (_gizmoSphere == ID.INVALID_ID || _listenerIconMaterial == ID.INVALID_ID) return;

            float fx = camX - px, fy = camY - py, fz = camZ - pz;
            float fl = (float)Math.Sqrt(fx * fx + fy * fy + fz * fz);
            if (fl < 0.05f) return;
            fx /= fl; fy /= fl; fz /= fl;
            float rx = fz, rz = -fx;
            float rl = (float)Math.Sqrt(rx * rx + rz * rz);
            if (rl < 0.01f) { rx = 1f; rz = 0f; rl = 1f; }
            rx /= rl; rz /= rl;
            float ux = fy * rz, uy = fz * rx - fx * rz, uz = -fy * rx;

            // Head (the _gizmoSphere mesh has radius 0.5 -> scale 0.28 = 0.14u radius).
            SubmitGizmoForRendering(_gizmoSphere, _listenerIconMaterial, BuildBasisMatrix(
                px, py, pz,
                rx, 0f, rz, 0.28f,
                ux, uy, uz, 0.28f,
                fx, fy, fz, 0.28f));

            // Ears on the billboard's horizontal axis, so they are always visible.
            for (int s = -1; s <= 1; s += 2)
            {
                SubmitGizmoForRendering(_gizmoCube, _listenerIconMaterial, BuildBasisMatrix(
                    px + rx * 0.155f * s, py, pz + rz * 0.155f * s,
                    rx, 0f, rz, 0.05f,
                    ux, uy, uz, 0.11f,
                    fx, fy, fz, 0.08f));
            }
        }

        /// <summary>Min (yellow) and max (orange) distance spheres for the selected AudioSource —
        /// the audible falloff band the designer is tuning. Fine wireframe nets, one draw each.</summary>
        public static void RenderAudioRangeSpheres(float cx, float cy, float cz, float minDist, float maxDist)
        {
            EnsureAudioGizmoResources();
            if (minDist > 0.005f)
                AudioWireSphere(cx, cy, cz, minDist, _audioMinMaterial);
            if (maxDist > minDist + 0.005f)
                AudioWireSphere(cx, cy, cz, maxDist, _audioMaxMaterial);
        }

        /// <summary>Boundary (cyan) + falloff shell (muted teal) for the selected ReverbZone, in the zone entity's world
        /// matrix <paramref name="world"/> (row-major, rows = the entity's scaled axes, 12..14 = position — TransformMath).
        /// shape: 0 = sphere (radius x the largest world axis), 1 = oriented box (half extents in local units); the
        /// falloff shell lies <paramref name="falloff"/> world metres outside along every box axis. Mirrors the runtime
        /// weight test (AudioPlaybackService.ZoneWeight, #497) — including the Max(0.01, size) clamp — so the drawn
        /// shape is exactly the audible one.</summary>
        public static void RenderReverbZoneGizmo(float[] world, int shape, float radius, float hx, float hy, float hz, float falloff)
        {
            if (world == null || world.Length < 16) return;
            EnsureAudioGizmoResources();
            float lx = AxisLen(world, 0), ly = AxisLen(world, 1), lz = AxisLen(world, 2);

            if (shape == 1)
            {
                if (lx < 1e-6f || ly < 1e-6f || lz < 1e-6f) return;   // a zero scale: the zone has no volume
                float[] half = { Math.Max(0.01f, hx) * lx, Math.Max(0.01f, hy) * ly, Math.Max(0.01f, hz) * lz };   // world metres
                AudioWireOrientedBox(world, half, 0f, _reverbZoneMaterial);
                if (falloff > 0.01f)
                    AudioWireOrientedBox(world, half, falloff, _reverbFalloffMaterial);
            }
            else
            {
                // Clamp exactly like the runtime weight test (ZoneWeight: max(0.01, radius) x largest scale), and build
                // the falloff shell on the CLAMPED radius so both surfaces match what is audible.
                float r = Math.Max(0.01f, radius) * Math.Max(lx, Math.Max(ly, lz));
                AudioWireSphere(world[12], world[13], world[14], r, _reverbZoneMaterial);
                if (falloff > 0.01f)
                    AudioWireSphere(world[12], world[13], world[14], r + falloff, _reverbFalloffMaterial);
            }
        }

        private static float AxisLen(float[] m, int row)
            => (float)Math.Sqrt(m[row * 4] * m[row * 4] + m[row * 4 + 1] * m[row * 4 + 1] + m[row * 4 + 2] * m[row * 4 + 2]);

        // Wireframe-net box along the world matrix's (unit) axes: half sizes in world metres, grown by `grow` metres per
        // axis (the falloff shell). _gizmoCube is a unit cube -> each row is unit axis x full size.
        private static void AudioWireOrientedBox(float[] world, float[] half, float grow, long material)
        {
            if (_gizmoCube == ID.INVALID_ID || material == ID.INVALID_ID) return;
            var m = new float[16];
            for (int row = 0; row < 3; row++)
            {
                float len = AxisLen(world, row);
                float size = 2f * (half[row] + grow) / len;
                m[row * 4] = world[row * 4] * size;
                m[row * 4 + 1] = world[row * 4 + 1] * size;
                m[row * 4 + 2] = world[row * 4 + 2] * size;
            }
            m[12] = world[12]; m[13] = world[13]; m[14] = world[14]; m[15] = 1f;
            SubmitGizmoWireForRendering(_gizmoCube, material, m);
        }
    }
}
