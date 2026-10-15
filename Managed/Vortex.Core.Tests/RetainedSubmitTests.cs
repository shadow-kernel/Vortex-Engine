using System;
using System.Collections.Generic;
using Editor.Core.Services;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components;
using Editor.ECS.Components.Rendering;

namespace VortexTests
{
    /// <summary>The retained scene submit (#361): after the first submit only what changed reaches the renderer — a move is
    /// one matrix update, a parent's move updates its subtree, (de)activation, destruction and a spawn add or remove exactly
    /// those instances, a material or colour change updates that instance; a frame where nothing changed sends nothing and
    /// walks nothing. The ops go to a recorder instead of the engine, meshes and materials come from a fake resolver.</summary>
    public static class RetainedSubmitTests
    {
        private sealed class Rig : IDisposable
        {
            public readonly SceneRenderService Svc = SceneRenderService.Instance;
            public readonly List<VortexAPI.RenderInstanceOp> Ops = new List<VortexAPI.RenderInstanceOp>();
            public uint Epoch = 7;
            private readonly SceneRenderService.InstanceSink _sink;
            private readonly bool _play;

            public Rig(bool play)
            {
                _sink = Svc.Sink;
                _play = PlayModeService.Instance.NativeGameHostRunning;
                PlayModeService.Instance.NativeGameHostRunning = play;   // play-like: the incremental path
                Svc.Sink = Record;
                Svc.Resolver = Resolve;
                Svc.DropRetained(clearNative: false);
                RenderChanges.Clear();
                RenderChanges.StructureChanged();
            }

            private bool Record(VortexAPI.RenderInstanceOp[] ops, int count, out uint epoch)
            {
                for (int i = 0; i < count; i++) Ops.Add(ops[i]);
                epoch = Epoch;
                return true;
            }

            // primitives get fixed mesh ids, a .vmat path its material id, no path the plain material; layer and tint as authored
            private void Resolve(GameEntity e, MeshRenderer mr)
            {
                if (!mr.IsEnabled || string.IsNullOrEmpty(mr.MeshPath) || e.Transform == null) return;
                long mesh = mr.MeshPath == "Primitive:Sphere" ? 2 : 1;
                long mat = string.IsNullOrEmpty(mr.MaterialPath) ? 100 : (mr.MaterialPath == "b.vmat" ? 102 : 101);
                Svc.AddResolved(mesh, mat, mr.RenderLayer, mr.ColorR, mr.ColorG, mr.ColorB, mr.ColorA);
            }

            /// <summary>One submit; returns its ops (and forgets them).</summary>
            public List<VortexAPI.RenderInstanceOp> Submit(Editor.Core.Data.Scene scene)
            {
                Ops.Clear();
                Svc.PrepareRetained(scene);
                Svc.FinishRetained();
                return new List<VortexAPI.RenderInstanceOp>(Ops);
            }

            public void Dispose()
            {
                Svc.DropRetained(clearNative: false);
                Svc.Sink = _sink;
                Svc.Resolver = null;
                PlayModeService.Instance.NativeGameHostRunning = _play;
                RenderChanges.StructureChanged();
            }
        }

        private static GameEntity Cube(Editor.Core.Data.Scene scene, string name, float x, GameEntity parent = null)
        {
            var e = new GameEntity(scene, name);
            e.AddComponentDirect(new MeshRenderer(e) { MeshPath = "Primitive:Cube" });
            e.Transform.LocalPosition = new Vector3(x, 0, 0);
            if (parent != null) { e.Parent = parent; parent.Children.Add(e); }
            else scene.Entities.Add(e);
            return e;
        }

        private static int Count(List<VortexAPI.RenderInstanceOp> ops, int kind)
        {
            int n = 0;
            foreach (var op in ops) if (op.Kind == kind) n++;
            return n;
        }

        /// <summary>The world matrix the old per-frame path built for an entity (SceneRenderService.BuildWorldMatrixInto):
        /// local × the parent's world matrix, recursively — the same order of float operations.</summary>
        private static float[] OldWorld(GameEntity e)
        {
            var local = new float[16];
            SceneRenderService.LocalMatrixInto(e.Transform, local);
            if (e.Parent?.Transform == null) return local;
            var dst = new float[16];
            SceneRenderService.MultiplyInto(local, OldWorld(e.Parent), dst);
            return dst;
        }

        private static bool SameMatrix(VortexAPI.RenderInstanceOp op, float[] m)
        {
            for (int i = 0; i < 16; i++) if (op.WorldAt(i) != m[i]) return false;
            return true;
        }

        [Test]
        public static void FirstSubmitCreatesTheVisibleInstancesThenNothing(TestContext t)
        {
            using var rig = new Rig(play: true);
            var scene = new Editor.Core.Data.Scene { Name = "Retained" };
            var a = Cube(scene, "A", 1);
            var b = Cube(scene, "B", 2);
            var off = Cube(scene, "Off", 3); off.IsActive = false;
            var noMesh = new GameEntity(scene, "Empty"); scene.Entities.Add(noMesh);

            var first = rig.Submit(scene);
            t.Equal(2, Count(first, VortexAPI.InstanceSet), "one SET per visible renderer (the inactive one and the empty entity send nothing)");
            t.Equal(0, Count(first, VortexAPI.InstanceRemove), "nothing to remove");
            bool sawA = false;
            foreach (var op in first) if (SameMatrix(op, OldWorld(a)) && op.Mesh == 1 && op.Material == 100) sawA = true;
            t.True(sawA, "A's instance carries its world matrix, the cube mesh and the plain material");
            t.Equal(2, SceneRenderService.LastRetainedInstances, "two instances retained");

            long walks = SceneRenderService.SceneWalks;
            var idle = rig.Submit(scene);
            t.Equal(0, idle.Count, "a frame where nothing changed sends nothing");
            t.Equal(walks, SceneRenderService.SceneWalks, "and walks nothing");
        }

        [Test]
        public static void AMoveIsOneMatrixUpdate(TestContext t)
        {
            using var rig = new Rig(play: true);
            var scene = new Editor.Core.Data.Scene { Name = "Retained" };
            var a = Cube(scene, "A", 1);
            for (int i = 0; i < 50; i++) Cube(scene, "Still " + i, 10 + i);
            rig.Submit(scene);

            long walks = SceneRenderService.SceneWalks;
            a.Transform.LocalPosition = new Vector3(5, 6, 7);
            var ops = rig.Submit(scene);
            t.Equal(1, ops.Count, "one op for one moved entity among 51");
            t.Equal(VortexAPI.InstanceMove, ops[0].Kind, "a move: the instance keeps its run");
            t.True(SameMatrix(ops[0], OldWorld(a)), "with the new world matrix");
            t.Equal(5f, ops[0].WorldAt(12), "translation x");
            t.Equal(walks, SceneRenderService.SceneWalks, "a move walks no scene");

            a.Transform.LocalRotation = new Vector3(0, 90, 0);
            a.Transform.LocalScale = new Vector3(2, 2, 2);
            ops = rig.Submit(scene);
            t.Equal(1, ops.Count, "rotation + scale in one frame: still one op");
            t.True(SameMatrix(ops[0], OldWorld(a)), "the rotated, scaled matrix");
        }

        [Test]
        public static void AParentMoveUpdatesItsSubtree(TestContext t)
        {
            using var rig = new Rig(play: true);
            var scene = new Editor.Core.Data.Scene { Name = "Retained" };
            var root = new GameEntity(scene, "Block");
            root.Transform.LocalPosition = new Vector3(100, 0, 0);
            scene.Entities.Add(root);
            var c1 = Cube(scene, "C1", 1, root);
            var c2 = Cube(scene, "C2", 2, root);
            var grand = Cube(scene, "G", 3, c2);
            var other = Cube(scene, "Other", 9);
            rig.Submit(scene);

            root.Transform.LocalPosition = new Vector3(200, 5, 0);
            root.Transform.LocalRotation = new Vector3(0, 45, 0);
            var ops = rig.Submit(scene);
            t.Equal(3, ops.Count, "the parent (no renderer) moves its two children and the grandchild — the rest stays");
            int matched = 0;
            foreach (var op in ops)
                if (SameMatrix(op, OldWorld(c1)) || SameMatrix(op, OldWorld(c2)) || SameMatrix(op, OldWorld(grand))) matched++;
            t.Equal(3, matched, "each with local × the moved parent chain");
            t.Equal(3, Count(ops, VortexAPI.InstanceMove), "all moves");

            c2.Transform.LocalScale = new Vector3(3, 3, 3);
            ops = rig.Submit(scene);
            t.Equal(2, ops.Count, "a middle entity's change moves it and its child, not its sibling");
        }

        [Test]
        public static void ActivationRemovesAndRestoresExactlyThoseInstances(TestContext t)
        {
            using var rig = new Rig(play: true);
            var scene = new Editor.Core.Data.Scene { Name = "Retained" };
            var parent = Cube(scene, "Door", 1);
            Cube(scene, "Hinge", 2, parent);
            for (int i = 0; i < 10; i++) Cube(scene, "Still " + i, 10 + i);
            rig.Submit(scene);

            parent.IsActive = false;
            var ops = rig.Submit(scene);
            t.Equal(2, Count(ops, VortexAPI.InstanceRemove), "deactivating a parent removes it and its child");
            t.Equal(2, ops.Count, "and nothing else");
            t.Equal(10, SceneRenderService.LastRetainedInstances, "ten left");

            parent.IsActive = true;
            ops = rig.Submit(scene);
            t.Equal(2, Count(ops, VortexAPI.InstanceSet), "activating brings both back");
            t.Equal(2, ops.Count, "and nothing else");
            t.Equal(12, SceneRenderService.LastRetainedInstances, "twelve again");
        }

        [Test]
        public static void DestroyAndSpawnAddOrRemoveOnlyThem(TestContext t)
        {
            using var rig = new Rig(play: true);
            var scene = new Editor.Core.Data.Scene { Name = "Retained" };
            var doomed = Cube(scene, "Crate", 1);
            Cube(scene, "Inside", 2, doomed);
            for (int i = 0; i < 10; i++) Cube(scene, "Still " + i, 10 + i);
            rig.Submit(scene);

            scene.Entities.Remove(doomed);   // what Scene.Destroy does to a root entity
            var ops = rig.Submit(scene);
            t.Equal(2, Count(ops, VortexAPI.InstanceRemove), "a destroyed entity's instances (and its child's) are removed");
            t.Equal(2, ops.Count, "nothing else is sent");

            var spawned = Cube(scene, "Bullet", 30);
            ops = rig.Submit(scene);
            t.Equal(1, Count(ops, VortexAPI.InstanceSet), "a spawn creates its instance");
            t.Equal(1, ops.Count, "nothing else is sent");
            t.True(SameMatrix(ops[0], OldWorld(spawned)), "at its spawn position");

            var child = Cube(scene, "Muzzle", 1, spawned);   // spawned under an entity
            ops = rig.Submit(scene);
            t.Equal(1, ops.Count, "a child added under a live entity: one SET");
            t.True(SameMatrix(ops[0], OldWorld(child)), "with the parent's matrix applied");
        }

        [Test]
        public static void MaterialAndColourChangesReachTheirInstance(TestContext t)
        {
            using var rig = new Rig(play: true);
            var scene = new Editor.Core.Data.Scene { Name = "Retained" };
            var a = Cube(scene, "A", 1);
            var b = Cube(scene, "B", 2);
            var first = rig.Submit(scene);
            int handleA = -1;
            foreach (var op in first) if (SameMatrix(op, OldWorld(a))) handleA = op.Handle;

            a.GetComponent<MeshRenderer>().ColorR = 0.25f;
            var ops = rig.Submit(scene);
            t.Equal(1, ops.Count, "a colour change: one op");
            t.Equal(VortexAPI.InstanceMove, ops[0].Kind, "the tint rides with the instance (#331): same run, no new material");
            t.Equal(0.25f, ops[0].ColorAt(0), "the new red");
            t.Equal(handleA, ops[0].Handle, "A's instance");

            a.GetComponent<MeshRenderer>().MaterialPath = "b.vmat";
            ops = rig.Submit(scene);
            t.Equal(1, ops.Count, "a material change: one op");
            t.Equal(VortexAPI.InstanceSet, ops[0].Kind, "a SET: the instance moves to the material's run");
            t.Equal(102L, ops[0].Material, "with the new material");
            t.Equal(handleA, ops[0].Handle, "the same instance");

            a.GetComponent<MeshRenderer>().IsEnabled = false;
            ops = rig.Submit(scene);
            t.Equal(1, Count(ops, VortexAPI.InstanceRemove), "a disabled renderer leaves");
            a.GetComponent<MeshRenderer>().MeshPath = "Primitive:Sphere";
            a.GetComponent<MeshRenderer>().IsEnabled = true;
            ops = rig.Submit(scene);
            t.Equal(1, Count(ops, VortexAPI.InstanceSet), "enabled again with another mesh: one SET");
            t.Equal(2L, ops[0].Mesh, "the sphere");
        }

        [Test]
        public static void ReparentingMovesTheInstanceUnderItsNewParent(TestContext t)
        {
            using var rig = new Rig(play: true);
            var scene = new Editor.Core.Data.Scene { Name = "Retained" };
            var left = new GameEntity(scene, "Left"); left.Transform.LocalPosition = new Vector3(-50, 0, 0); scene.Entities.Add(left);
            var right = new GameEntity(scene, "Right"); right.Transform.LocalPosition = new Vector3(50, 0, 0); scene.Entities.Add(right);
            var item = Cube(scene, "Item", 1, left);
            rig.Submit(scene);

            left.Children.Remove(item);
            item.Parent = right;
            right.Children.Add(item);
            var ops = rig.Submit(scene);
            bool moved = false;
            foreach (var op in ops) if (op.Kind != VortexAPI.InstanceRemove && SameMatrix(op, OldWorld(item))) moved = true;
            t.True(moved, "after the re-parent the instance carries the new parent's matrix");
            t.Equal(51f, OldWorld(item)[12], "(the item now sits at the right parent)");
            t.Equal(1, SceneRenderService.LastRetainedInstances, "still one instance");
        }

        [Test]
        public static void EditModeWalksEverySubmitButSendsOnlyDifferences(TestContext t)
        {
            using var rig = new Rig(play: false);
            var scene = new Editor.Core.Data.Scene { Name = "Retained" };
            var a = Cube(scene, "A", 1);
            var hidden = Cube(scene, "Hidden", 2);
            hidden.IsHiddenInEditor = true;   // the eye toggle: edit mode only
            var first = rig.Submit(scene);
            t.Equal(1, Count(first, VortexAPI.InstanceSet), "the hidden entity is not drawn in edit mode");

            long walks = SceneRenderService.SceneWalks;
            var again = rig.Submit(scene);
            t.Equal(walks + 1, SceneRenderService.SceneWalks, "edit mode walks the scene on each of its submits");
            t.Equal(0, again.Count, "but sends nothing when nothing changed");

            a.Transform.LocalPosition = new Vector3(8, 0, 0);
            var ops = rig.Submit(scene);
            t.Equal(1, ops.Count, "a gizmo drag: one op");
            t.True(SameMatrix(ops[0], OldWorld(a)), "with the dragged matrix");
        }

        [Test]
        public static void ANewRendererEpochSendsEverythingAgain(TestContext t)
        {
            using var rig = new Rig(play: true);
            var scene = new Editor.Core.Data.Scene { Name = "Retained" };
            for (int i = 0; i < 5; i++) Cube(scene, "C" + i, i);
            rig.Submit(scene);
            rig.Epoch++;   // the renderer dropped its instances (scene switch, a destroyed mesh)
            var ops = rig.Submit(scene);
            t.Equal(5, Count(ops, VortexAPI.InstanceSet), "every instance is sent again");
            t.Equal(5, SceneRenderService.LastRetainedInstances, "five retained");
        }

        [Test]
        public static void TheCachedWorldMatrixFollowsTheChain(TestContext t)
        {
            var scene = new Editor.Core.Data.Scene { Name = "World" };
            var root = new GameEntity(scene, "Root");
            root.Transform.LocalPosition = new Vector3(3, 4, 5);
            root.Transform.LocalRotation = new Vector3(10, 20, 30);
            root.Transform.LocalScale = new Vector3(1, 2, 3);
            var child = new GameEntity(scene, "Child"); child.Parent = root; root.Children.Add(child);
            child.Transform.LocalPosition = new Vector3(-1, 0.5f, 2);
            child.Transform.LocalRotation = new Vector3(0, -45, 5);
            var leaf = new GameEntity(scene, "Leaf"); leaf.Parent = child; child.Children.Add(leaf);
            leaf.Transform.LocalPosition = new Vector3(0, 1, 0);

            bool Same(float[] a, float[] b) { for (int i = 0; i < 16; i++) if (a[i] != b[i]) return false; return true; }
            t.True(Same(leaf.Transform.WorldMatrix, OldWorld(leaf)), "the cached world matrix is bit for bit the one the old path built");
            int v = leaf.Transform.WorldVersion;
            t.Equal(v, leaf.Transform.WorldVersion, "reading it again changes nothing");

            root.Transform.LocalPosition = new Vector3(7, 4, 5);   // an ancestor moved
            t.True(leaf.Transform.WorldVersion != v, "the leaf sees its grandparent's move");
            t.True(Same(leaf.Transform.WorldMatrix, OldWorld(leaf)), "and its matrix follows");

            v = leaf.Transform.WorldVersion;
            var other = new GameEntity(scene, "Other"); other.Transform.LocalPosition = new Vector3(100, 0, 0);
            child.Parent = other; root.Children.Remove(child); other.Children.Add(child);   // re-parented
            t.True(leaf.Transform.WorldVersion != v, "a re-parent above is seen too");
            t.True(Same(leaf.Transform.WorldMatrix, OldWorld(leaf)), "under the new chain");
        }
    }
}
