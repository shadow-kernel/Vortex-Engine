using System.Collections.Generic;
using Editor.Core.Services;
using Editor.ECS;
using Editor.ECS.Components;

namespace VortexTests
{
    /// <summary>The static / dynamic split of the play-mode scene submit (#364 A): which entities are walked every
    /// frame, which go into the renderer's retained static set, and when a static transform change is noticed.</summary>
    public static class StaticSplitTests
    {
        private static GameEntity Entity(string name, GameEntity parent = null)
        {
            var e = new GameEntity { Name = name };
            if (parent != null) parent.AddChild(e);
            return e;
        }

        [Test]
        public static void PlainEntitiesAreStaticAndChildrenAreVisited(TestContext t)
        {
            var root = Entity("Room");
            var lamp = Entity("Lamp", root);
            var bulb = Entity("Bulb", lamp);
            var hidden = Entity("Off", root); hidden.IsActive = false;
            var skipped = Entity("Child of off", hidden);

            var dynamic = new List<GameEntity>(); var statics = new List<GameEntity>();
            SceneRenderService.Classify(root, false, dynamic, statics.Add);

            t.Equal(0, dynamic.Count, "nothing moves every frame");
            t.Equal(3, statics.Count, "root, lamp and bulb are static");
            t.False(root.RenderDynamic || lamp.RenderDynamic || bulb.RenderDynamic, "flagged static");
            t.False(statics.Contains(hidden) || statics.Contains(skipped), "an inactive subtree is not submitted");
        }

        [Test]
        public static void AnAncestorThatMovesMakesTheSubtreeDynamic(TestContext t)
        {
            var root = Entity("World");
            var car = Entity("Car", root);
            var wheel = Entity("Wheel", car);
            var tree = Entity("Tree", root);

            var dynamic = new List<GameEntity>(); var statics = new List<GameEntity>();
            // the classification asks the entity itself; simulate "car has a Rigidbody" through the walk's parent flag
            SceneRenderService.Classify(root, false, dynamic, statics.Add);
            t.Equal(0, dynamic.Count, "no per-frame components yet");

            dynamic.Clear(); statics.Clear();
            SceneRenderService.Classify(car, true, dynamic, statics.Add);   // as if an ancestor were dynamic
            t.Equal(2, dynamic.Count, "car and its wheel are walked every frame");
            t.True(car.RenderDynamic && wheel.RenderDynamic, "flagged dynamic");
            t.Equal(0, statics.Count, "none of that subtree is in the static set");
            t.False(tree.RenderDynamic, "the tree is untouched");
        }

        [Test]
        public static void StaticTransformsBumpTheVersionDynamicOnesDoNot(TestContext t)
        {
            var still = Entity("Crate");
            var moving = Entity("Player"); moving.RenderDynamic = true;
            // a bare entity has no Transform yet (the scene factory adds one) — give both one
            var ts = new Transform { Entity = still }; still.Components.Add(ts);
            var tm = new Transform { Entity = moving }; moving.Components.Add(tm);

            int v0 = Transform.StaticVersion;
            tm.LocalPosition = new Vector3(1, 2, 3);
            t.Equal(v0, Transform.StaticVersion, "a dynamic entity moving is not a static change");
            ts.LocalPosition = new Vector3(4, 5, 6);
            t.True(Transform.StaticVersion > v0, "a static entity moving re-sends the static set");
            int v1 = Transform.StaticVersion;
            ts.LocalPosition = new Vector3(4, 5, 6);
            t.Equal(v1, Transform.StaticVersion, "setting the same value again changes nothing");
        }
    }
}
