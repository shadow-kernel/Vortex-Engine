using System;
using System.Collections.Generic;
using Editor.Core.Services;
using Editor.ECS;
using Editor.ECS.Components;

namespace VortexTests
{
    /// <summary>The scene-submit fast path (8–10k entities): rigid submits grouped per (mesh, material, layer) into
    /// one call each, and world matrices built without allocating — both must give exactly the same data the old
    /// per-entity path sent.</summary>
    public static class RenderSubmitTests
    {
        private static float[] Translation(float x, float y, float z)
        {
            var m = new float[16];
            SceneRenderService.IdentityInto(m);
            m[12] = x; m[13] = y; m[14] = z;
            return m;
        }

        private sealed class Sent { public long Mesh, Mat; public int Layer, Count; public float[] Data; }

        private static List<Sent> Drain(SceneRenderService.InstanceBatcher b)
        {
            var sent = new List<Sent>();
            b.Flush((mesh, mat, layer, data, count) => sent.Add(new Sent { Mesh = mesh, Mat = mat, Layer = layer, Count = count, Data = (float[])data.Clone() }));
            return sent;
        }

        [Test]
        public static void BatcherGroupsByMeshMaterialAndLayer(TestContext t)
        {
            var b = new SceneRenderService.InstanceBatcher();
            b.Add(1, 10, 0, Translation(1, 0, 0));
            b.Add(2, 10, 0, Translation(2, 0, 0));
            b.Add(1, 10, 0, Translation(3, 0, 0));
            b.Add(1, 10, 1, Translation(4, 0, 0));   // same mesh+material, other layer = other group
            b.Add(1, 11, 0, Translation(5, 0, 0));   // other material = other group
            b.Add(1, 10, 0, Translation(6, 0, 0));
            t.Equal(4, b.BatchCount, "four distinct (mesh, material, layer) groups");
            t.Equal(6, b.InstanceCount, "six instances queued");

            var sent = Drain(b);
            t.Equal(4, sent.Count, "one call per group");
            var g = sent[0];
            t.Equal(1L, g.Mesh, "first-seen group first"); t.Equal(10L, g.Mat, "its material"); t.Equal(0, g.Layer, "its layer");
            t.Equal(3, g.Count, "three instances of mesh 1 / material 10 / layer 0");
            t.Equal(1f, g.Data[0 * 16 + 12], "instance 0 translation kept in submit order");
            t.Equal(3f, g.Data[1 * 16 + 12], "instance 1");
            t.Equal(6f, g.Data[2 * 16 + 12], "instance 2");
            t.Equal(1, sent[2].Layer, "the layer-1 group is its own call");
            t.Equal(11L, sent[3].Mat, "the material-11 group is its own call");
            t.Equal(0, b.BatchCount, "flushed: no open groups");
            t.Equal(0, b.InstanceCount, "flushed: counter reset");
        }

        [Test]
        public static void BatcherReusesBuffersAcrossPasses(TestContext t)
        {
            var b = new SceneRenderService.InstanceBatcher();
            for (int i = 0; i < 40; i++) b.Add(7, 1, 0, Translation(i, 0, 0));   // grows past the initial 16 slots
            var pass1 = Drain(b);
            t.Equal(1, pass1.Count, "one group"); t.Equal(40, pass1[0].Count, "forty instances");
            t.Equal(39f, pass1[0].Data[39 * 16 + 12], "the last instance survived the buffer growth");

            for (int i = 0; i < 5; i++) b.Add(7, 1, 0, Translation(100 + i, 0, 0));
            var pass2 = Drain(b);
            t.Equal(1, pass2.Count, "one group again (pooled batch reused)");
            t.Equal(5, pass2[0].Count, "count reset for the new pass");
            t.Equal(100f, pass2[0].Data[12], "pass-2 data, not pass-1 leftovers");
            t.Equal(104f, pass2[0].Data[4 * 16 + 12], "last pass-2 instance");
        }

        [Test]
        public static void IdentityAndMultiply(TestContext t)
        {
            var id = new float[16];
            SceneRenderService.IdentityInto(id);
            for (int i = 0; i < 16; i++) t.Equal(i % 5 == 0 ? 1f : 0f, id[i], "identity element " + i);

            // row-major: translation (1,2,3) × uniform scale 2 => scaled axes, translation doubled
            var a = Translation(1, 2, 3);
            var s = new float[16]; SceneRenderService.IdentityInto(s); s[0] = s[5] = s[10] = 2f;
            var dst = new float[16];
            SceneRenderService.MultiplyInto(a, s, dst);
            t.Equal(2f, dst[0], "x axis scaled"); t.Equal(2f, dst[5], "y axis scaled"); t.Equal(2f, dst[10], "z axis scaled");
            t.Equal(2f, dst[12], "tx doubled"); t.Equal(4f, dst[13], "ty doubled"); t.Equal(6f, dst[14], "tz doubled");
            t.Equal(1f, dst[15], "w");
        }

        [Test]
        public static void LocalMatrixMatchesTheTransform(TestContext t)
        {
            var tr = new Transform { LocalPosition = new Vector3(1, 2, 3), LocalRotation = new Vector3(0, 0, 0), LocalScale = new Vector3(2, 3, 4) };
            var m = new float[16];
            SceneRenderService.LocalMatrixInto(tr, m);
            t.Equal(2f, m[0], "scale x"); t.Equal(3f, m[5], "scale y"); t.Equal(4f, m[10], "scale z");
            t.Equal(1f, m[12], "pos x"); t.Equal(2f, m[13], "pos y"); t.Equal(3f, m[14], "pos z"); t.Equal(1f, m[15], "w");
            t.Equal(0f, m[1], "no shear"); t.Equal(0f, m[4], "no shear");

            // 90° yaw: the local X axis turns to -Z, the local Z axis to +X (the engine's ZXY Euler order)
            tr.LocalRotation = new Vector3(0, 90, 0); tr.LocalScale = new Vector3(1, 1, 1);
            SceneRenderService.LocalMatrixInto(tr, m);
            t.True(Math.Abs(m[0]) < 1e-5f && Math.Abs(m[2] + 1f) < 1e-5f, "x axis -> -z");
            t.True(Math.Abs(m[8] - 1f) < 1e-5f && Math.Abs(m[10]) < 1e-5f, "z axis -> +x");
            t.Equal(1f, m[5], "y axis unchanged");
        }
    }
}
