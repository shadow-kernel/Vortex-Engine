using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Foliage;
using Editor.Core.Services;
using Editor.Core.Services.Foliage;
using Editor.Core.Services.Terrain;
using Editor.Core.Terrain;
using Editor.Core.Viewport;
using Editor.ECS;
using Editor.ECS.Components.Rendering;

namespace VortexEditor.Shell
{
    /// <summary>Painted foliage on the real backend (#125): a flat 64 m terrain in front of the editor camera gets a foliage
    /// layer with a sphere "bush" type painted over it — the instances must land on the terrain surface with the type's
    /// spacing, draw through the instancing path (the frame changes), cull with distance, erase inside a disc, round-trip
    /// through the data file, and feed the collision world as capsules.</summary>
    internal static class FoliageSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("foliage", Run);

        private static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            var scene = ProjectData.Current?.ActiveScene;
            if (scene == null) { log.Log("foliage: no scene — skipped"); return true; }
            if (!Editor.DllWrapper.VortexAPI.TerrainApiAvailable) { log.LogError("foliage: the engine library has no terrain exports"); return false; }
            var cam = EditorCameraController.Instance;
            float px = cam.PositionX, py = cam.PositionY, pz = cam.PositionZ, yaw = cam.Yaw, pitch = cam.Pitch;
            var made = new List<GameEntity>();
            try
            {
                // a flat terrain at y = 498 under the camera's view
                var te = new GameEntity(scene, "SmokeFoliageGround");
                scene.AddEntity(te);
                made.Add(te);
                te.Transform.LocalPosition = new Vector3(-32f, 498f, -32f);
                te.AddComponent(new Editor.ECS.Components.Rendering.Terrain(te) { Size = 64f, Resolution = 33, Collision = true });
                TerrainData tdata; float cell;
                if (!TerrainService.TryGetData(te, out tdata, out cell)) { log.LogError("foliage: no terrain data"); return false; }
                tdata.Raise(16f, 16f, 10f, 2f, 0.2f);
                TerrainService.MarkDirty(te, new SampleRect { X0 = 0, Z0 = 0, X1 = 32, Z1 = 32 }, true, false);

                cam.SetPositionAndRotation(0, 503, -22, 0, 0);   // looking down +Z over the plot
                EditorViewportSession.RequestResubmit();
                await Task.Delay(300);
                EditorViewportSession.RequestResubmit();
                var before = await CameraSkySmoke.Sample("foliage_off.bmp", 0.5, 0.62);

                var fe = new GameEntity(scene, "SmokeFoliage");
                scene.AddEntity(fe);
                made.Add(fe);
                var f = new Editor.ECS.Components.Rendering.Foliage(fe);
                var bush = Editor.ECS.Components.Rendering.Foliage.DefaultType("Bush", "Primitive:Sphere", true);
                bush.Density = 0.6f; bush.MinSpacing = 1.2f; bush.MinScale = 1.4f; bush.MaxScale = 2.2f; bush.CullDistance = 120f; bush.ThinDistance = 0f; bush.Collision = 1; bush.CollisionRadius = 0.4f; bush.CollisionHeight = 1.5f; bush.Cutout = false; bush.WindStrength = 0.1f; bush.WindHeight = 1f;
                f.Types.Add(bush);
                fe.AddComponent(f);
                int placed = FoliageService.Paint(fe, 0, new System.Numerics.Vector3(0f, 0f, 0f), 14f, 1f, 7);
                FoliageData data;
                FoliageService.TryGetData(fe, out data);
                var layer = data.Layer("Bush", false);

                // every instance sits on the terrain surface (minus its sink) and keeps the spacing
                bool onSurface = true, spaced = true;
                if (layer != null)
                {
                    for (int i = 0; i < layer.Count && onSurface; i++)
                    {
                        var p = layer.Instances[i].Position;
                        float y;
                        if (!TerrainService.TryHeight(te, p.X, p.Z, out y) || Math.Abs(y - bush.Sink * layer.Instances[i].Scale - p.Y) > 0.05f) onSurface = false;
                    }
                    for (int i = 0; i < layer.Count && spaced; i++)
                        for (int j = i + 1; j < layer.Count; j++)
                        {
                            var a = layer.Instances[i].Position; var b = layer.Instances[j].Position;
                            float dx = a.X - b.X, dz = a.Z - b.Z;
                            if (dx * dx + dz * dz < bush.MinSpacing * bush.MinSpacing * 0.98f) { spaced = false; break; }
                        }
                }

                EditorViewportSession.RequestResubmit();
                await Task.Delay(300);
                EditorViewportSession.RequestResubmit();
                var after = await CameraSkySmoke.Sample("foliage_on.bmp", 0.5, 0.62);
                int drawn = FoliageService.LastInstancesDrawn, calls = FoliageService.LastDrawCalls;

                // culling: from 500 m away nothing of the type draws
                cam.SetPositionAndRotation(0, 503, -600, 0, 0);
                EditorViewportSession.RequestResubmit();
                await Task.Delay(250);
                EditorViewportSession.RequestResubmit();
                await Task.Delay(120);
                int drawnFar = FoliageService.LastInstancesDrawn;
                cam.SetPositionAndRotation(0, 503, -22, 0, 0);

                // erase a disc, round-trip the file, collision capsules
                int erased = FoliageService.Erase(fe, -1, new System.Numerics.Vector3(0f, 0f, 0f), 5f);
                int left = FoliageService.InstanceCount(fe);
                var bytes = data.ToBytes();
                var back = FoliageData.FromBytes(bytes);
                bool roundTrip = back != null && back.TotalCount == left && back.Layer("Bush", false) != null;
                var cols = FoliageService.Collidables(fe);
                bool capsules = cols.Count == left && (left == 0 || Math.Abs(cols[0].Height - bush.CollisionHeight * layer.Instances[0].Scale) < 1e-3f);
                int apiCount = Vortex.Foliage.InstanceCount(fe.EntityId, "Bush");

                log.Log("foliage: " + placed + " bushes painted (" + (onSurface ? "on the ground" : "FLOATING") + ", " + (spaced ? "spaced" : "TOO CLOSE") + "), "
                    + drawn + " drawn in " + calls + " calls, sample " + CameraSkySmoke.Rgb(before) + " -> " + CameraSkySmoke.Rgb(after) + ", far " + drawnFar
                    + ", erased " + erased + " -> " + left + " (api " + apiCount + "), file " + bytes.Length + " B " + (roundTrip ? "ok" : "BROKEN") + ", capsules " + cols.Count);

                bool painted = placed >= 20;
                bool visible = after.r >= 0 && (Math.Abs(after.r - before.r) + Math.Abs(after.g - before.g) + Math.Abs(after.b - before.b)) > 25;
                bool drew = drawn >= placed * 0.9f && calls >= 1;
                bool culled = drawnFar == 0;
                bool erasedOk = erased > 0 && left == placed - erased && apiCount == left;
                if (!painted) log.LogError("foliage: too few instances painted (" + placed + ")");
                if (!onSurface) log.LogError("foliage: instances float above / sink below the terrain");
                if (!spaced) log.LogError("foliage: the minimum spacing is violated");
                if (!visible) log.LogError("foliage: the bushes are not visible (the frame did not change)");
                if (!drew) log.LogError("foliage: the instances were not submitted (" + drawn + ")");
                if (!culled) log.LogError("foliage: instances draw beyond the cull distance (" + drawnFar + ")");
                if (!erasedOk) log.LogError("foliage: erase / count mismatch");
                if (!roundTrip) log.LogError("foliage: the data file does not round-trip");
                if (!capsules) log.LogError("foliage: the collision capsules do not match the instances");
                return painted && onSurface && spaced && visible && drew && culled && erasedOk && roundTrip && capsules;
            }
            finally
            {
                if (made.Count > 0) EditorCommands.DeleteEntities(made);
                cam.SetPositionAndRotation(px, py, pz, yaw, pitch);
                EditorViewportSession.RequestResubmit();
            }
        }
    }
}
