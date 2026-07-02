using System;
using Editor.Utilities;

namespace Editor.DllWrapper
{
    /// <summary>Audio gizmos (issue #18): camera-facing speaker/listener icons at every audio entity, fine
    /// WIREFRAME-net min/max distance spheres for the selected AudioSource and the reverb-zone boundary
    /// (+falloff shell) for a selected ReverbZone. Shapes go through the gizmo WIRE pass (one draw per whole
    /// sphere — thin GPU wireframe lines, not fat tube segments), all materials are UNLIT so the colors stay
    /// bright and readable from every angle. Submitted every edit-mode frame, so inspector edits and entity
    /// drags update live. The zone box is drawn axis-aligned because the runtime weight test
    /// (AudioPlaybackService.ZoneWeight) is axis-aligned.</summary>
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

        // Axis-aligned wireframe-net box (_gizmoCube is a unit cube -> scale = full extents).
        private static void AudioWireBox(float cx, float cy, float cz, float hx, float hy, float hz, long material)
        {
            if (_gizmoCube == ID.INVALID_ID || material == ID.INVALID_ID) return;
            SubmitGizmoWireForRendering(_gizmoCube, material, new float[]
            {
                hx * 2f, 0, 0, 0,
                0, hy * 2f, 0, 0,
                0, 0, hz * 2f, 0,
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

        /// <summary>Boundary (cyan) + falloff shell (muted teal) for the selected ReverbZone.
        /// shape: 0 = sphere (radius), 1 = box (half extents). Axis-aligned by design.</summary>
        public static void RenderReverbZoneGizmo(float cx, float cy, float cz, int shape,
            float radius, float hx, float hy, float hz, float falloff)
        {
            EnsureAudioGizmoResources();

            if (shape == 1)
            {
                AudioWireBox(cx, cy, cz, hx, hy, hz, _reverbZoneMaterial);
                if (falloff > 0.01f)
                    AudioWireBox(cx, cy, cz, hx + falloff, hy + falloff, hz + falloff, _reverbFalloffMaterial);
            }
            else
            {
                // Clamp exactly like the runtime weight test (ZoneWeight: max(0.01, radius)), and build the
                // falloff shell on the CLAMPED radius so both surfaces match what is audible.
                float r = Math.Max(0.01f, radius);
                AudioWireSphere(cx, cy, cz, r, _reverbZoneMaterial);
                if (falloff > 0.01f)
                    AudioWireSphere(cx, cy, cz, r + falloff, _reverbFalloffMaterial);
            }
        }
    }
}
