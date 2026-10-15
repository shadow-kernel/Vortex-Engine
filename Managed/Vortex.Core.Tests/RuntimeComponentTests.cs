using System;
using System.Linq;
using System.Reflection;
using Editor.Core.Data;
using Editor.Core.Serialization;
using Editor.Core.Services;
using Editor.Core.Services.Physics;
using Editor.ECS;
using Editor.ECS.Components.Lighting;
using Editor.ECS.Components.Physics;
using Editor.ECS.Components.Rendering;
using Editor.ECS.Components.Scripting;
using Editor.Scripting;

namespace VortexTests
{
    /// <summary>Components added and removed while the game runs (#421): by name, refused with a reason where it makes no
    /// sense, in the engine at once (the character world here; the Jolt bodies in ZScriptingPhysicsTests), and gone again —
    /// the scene as authored — when play ends, with or without the editor's play snapshot.</summary>
    public static class RuntimeComponentTests
    {
        private const string Scripts = @"
using Vortex;
public class Idle : VortexBehaviour { }
";

        private static Assembly Compile(TestContext t)
        {
            var file = t.Write("Scripts/Idle.cs", Scripts);
            var asm = RoslynScriptCompiler.Compile(new[] { file }, out var log);
            if (asm == null) throw new TestFailure("the test scripts do not compile: " + log);
            return asm;
        }

        private static bool Warned(string text) =>
            ConsoleService.Instance.Entries.Any(x => x.Level == LogLevel.Warning && x.Message.Contains(text));

        [Test]
        public static void ComponentTypesResolveByTheirInspectorNames(TestContext t)
        {
            t.Equal(typeof(Rigidbody), ScriptRuntime.ComponentTypeByName("Rigidbody"), "class name");
            t.Equal(typeof(BoxCollider), ScriptRuntime.ComponentTypeByName("box collider"), "Inspector name, any case");
            t.Equal(typeof(MeshRenderer), ScriptRuntime.ComponentTypeByName("Mesh Renderer"), "with a space");
            t.Equal(typeof(Editor.ECS.Components.Audio.AudioSource), ScriptRuntime.ComponentTypeByName("audio_source"), "underscores ignored");
            t.Equal(typeof(Collider), ScriptRuntime.ComponentTypeByName("Collider"), "a base type");
            t.True(ScriptRuntime.ComponentTypeByName("Flux Capacitor") == null, "unknown");
        }

        [Test]
        public static void AddAndRemoveFollowTheRulesAndPlayEndUndoesThem(TestContext t)
        {
            var asm = Compile(t);
            var scene = new Scene { Name = "Runtime" };
            var lamp = new GameEntity(scene, "Lamp"); scene.Entities.Add(lamp);
            lamp.Transform.LocalPosition = new Vector3(5f, 0f, 0f);
            var wall = new GameEntity(scene, "Wall"); scene.Entities.Add(wall);
            wall.AddComponentDirect(new Light(wall));
            wall.AddComponentDirect(new BoxCollider(wall));
            var actor = new GameEntity(scene, "Actor"); scene.Entities.Add(actor);
            actor.AddComponentDirect(new Script(actor, "Assets/Scripts/Idle.cs"));
            PhysicsRaycastTests.AsLoaded(scene);
            string before = DataSerializer.ToJson(scene);
            var snapshot = PlaySnapshot.Take(scene);
            var rt = ScriptRuntime.Instance;
            try
            {
                rt.Begin(scene, asm);
                long lampId = rt.HandleForEntity(lamp), wallId = rt.HandleForEntity(wall);

                t.True(Vortex.Scene.AddComponent(lampId, "Light"), "a Light added while playing");
                t.True(Vortex.Scene.HasComponent(lampId, "light") && lamp.GetComponent<Light>() != null, "… is on the entity");
                var light = Vortex.Scene.GetLight(lampId);
                t.NotNull(light, "… and Scene.GetLight hands it out at once");
                light.Intensity = 9f;
                t.False(Vortex.Scene.AddComponent(lampId, "Light"), "a second Light is refused (one per entity)");
                t.True(Warned("one per entity"), "… with the reason");
                t.False(Vortex.Scene.AddComponent(lampId, "Flux Capacitor"), "an unknown type is refused");
                t.True(Warned("there is no component type of that name"), "… with the reason and the type list");
                t.False(Vortex.Scene.AddComponent(lampId, "Transform"), "a Transform is refused");
                t.False(Vortex.Scene.AddComponent(lampId, "Script"), "a Script is refused (scripts come with their entity)");
                t.False(Vortex.Scene.AddComponent(lampId, "Collider"), "the collider base type is refused (name a shape)");
                t.False(Vortex.Scene.RemoveComponent(rt.HandleForEntity(actor), "Script"), "removing a Script is refused (Enabled)");
                t.False(Vortex.Scene.AddComponent(0, "Light"), "an unknown entity");

                // the character world takes a collider added while playing: a ray from above meets the lamp's new box
                t.True(Vortex.Scene.AddComponent(lampId, "BoxCollider"), "a BoxCollider added");
                bool hit = CollisionService.Raycast(new Vector3(5f, 10f, 0f), new Vector3(0f, -1f, 0f), 20f, ~0, out _, out _, out var who, out _);
                t.True(hit && who == lamp, "the new collider is in the collision world at once (hit " + who?.Name + ")");
                t.True(Vortex.Scene.RemoveComponent(lampId, "Collider"), "the collider removed again ('Collider' = any collider)");
                hit = CollisionService.Raycast(new Vector3(5f, 10f, 0f), new Vector3(0f, -1f, 0f), 20f, ~0, out _, out _, out who, out _);
                t.True(!hit || who != lamp, "… and gone from the collision world");

                // an authored component taken off and put back in its slot when play ends
                int lightSlot = wall.Components.IndexOf(wall.GetComponent<Light>());
                t.True(Vortex.Scene.RemoveComponent(wallId, "Light"), "the wall's authored Light removed");
                t.False(Vortex.Scene.HasComponent(wallId, "Light"), "… it has none now");
                t.False(Vortex.Scene.RemoveComponent(wallId, "Light"), "a second removal finds none");
                rt.End();
                t.True(lamp.GetComponent<Light>() == null, "play end: the added Light is gone");
                t.Equal(lightSlot, wall.Components.IndexOf(wall.GetComponent<Light>()), "play end: the removed Light is back in its slot");
                snapshot.Restore();
                t.Equal(before, DataSerializer.ToJson(scene), "the saved scene is exactly as authored (#508)");
            }
            finally { rt.End(); }
        }

        [Test]
        public static void AMeshRendererAddedWhilePlayingTakesAMesh(TestContext t)
        {
            var asm = Compile(t);
            var scene = new Scene { Name = "Mesh" };
            var e = new GameEntity(scene, "Bare"); scene.Entities.Add(e);
            var rt = ScriptRuntime.Instance;
            try
            {
                rt.Begin(scene, asm);
                long id = rt.HandleForEntity(e);
                t.False(Vortex.Scene.SetMeshOf(id, "Primitive:Cube"), "no Mesh Renderer: SetMeshOf says so");
                t.True(Vortex.Scene.AddComponent(id, "Mesh Renderer"), "a Mesh Renderer added");
                t.True(Vortex.Scene.SetMeshOf(id, "Primitive:Sphere"), "… it takes a mesh");
                t.Equal("Primitive:Sphere", Vortex.Scene.MeshOf(id), "MeshOf reads it back");
                t.True(SceneRenderService.StaticDirty, "the scene is re-submitted (structural change)");
            }
            finally { rt.End(); }
            t.True(e.GetComponent<MeshRenderer>() == null, "play end takes it off again");
        }
    }
}
