using System;
using System.Collections.Generic;
using System.Numerics;
using Editor.Core.Animation;

namespace VortexTests
{
    /// <summary>Root motion (#113): the root bone's travel is read from the clip (across a loop wrap too), expressed in
    /// model space through the armature's bind pose, and the root-motion bone is found by name or rig shape.</summary>
    public static class RootMotionTests
    {
        // a two-level rig: an armature root rotated like a Mixamo FBX import (local Z up → model Y up), a hips child
        private static SkeletonDef Rig(string hipsName = "mixamorig:Hips", bool rotatedArmature = true)
        {
            var rot = rotatedArmature ? Quaternion.CreateFromAxisAngle(Vector3.UnitX, -(float)Math.PI / 2) : Quaternion.Identity;
            var root = new SkeletonNode { Name = "Armature", Parent = -1, BindRotation = rot, BindTranslation = Vector3.Zero, BindScale = Vector3.One };
            root.LocalBind = Matrix4x4.CreateFromQuaternion(rot);
            var hips = new SkeletonNode { Name = hipsName, Parent = 0, BindTranslation = new Vector3(0, 0, 1f), BindRotation = Quaternion.Identity, BindScale = Vector3.One };
            hips.LocalBind = Matrix4x4.CreateTranslation(hips.BindTranslation);
            var spine = new SkeletonNode { Name = "mixamorig:Spine", Parent = 1, BindTranslation = new Vector3(0, 0.2f, 0), BindRotation = Quaternion.Identity, BindScale = Vector3.One };
            spine.LocalBind = Matrix4x4.CreateTranslation(spine.BindTranslation);
            return new SkeletonDef
            {
                Nodes = new[] { root, hips, spine },
                Bones = new[] { new SkeletonBone { NodeIndex = 1 }, new SkeletonBone { NodeIndex = 2 } },
            };
        }

        // the hips travel +Y in the root's LOCAL space (= forward / model +Z after the −90° X armature rotation), 2 m per second
        private static VortexAnimClip WalkClip()
        {
            var clip = new VortexAnimClip { Name = "walk", DurationSec = 1f, Loop = true };
            var track = new AnimTrack { Bone = "mixamorig:Hips" };
            for (int i = 0; i <= 4; i++)
            {
                float t = i * 0.25f;
                track.Pos.Add(new AnimKeyVec3 { T = t, X = 0f, Y = 2f * t, Z = 1f });
            }
            clip.Tracks.Add(track);
            return clip;
        }

        [Test]
        public static void RootDeltaFollowsTheClipAcrossWraps(TestContext t)
        {
            var skel = Rig();
            var clip = WalkClip();
            var nodes = AnimationService.ResolveTrackNodes(skel, clip);
            int root = AnimationService.FindRootMotionNode(skel, "");
            t.Equal(1, root, "hips detected by name");
            t.Equal(1, nodes[0], "the track resolves to the hips node");
            // a normal step: 0.1 s → 0.2 m along the hips' local +Y, expressed in MODEL space through the armature's bind
            // rotation (−90° about X turns local +Y into model −Z: the character walks along −Z in this rig)
            var step = Vector3.TransformNormal(new Vector3(0f, 0.2f, 0f), skel.Nodes[0].LocalBind);
            t.True(Math.Abs(step.Z + 0.2f) < 1e-4f && Math.Abs(step.Y) < 1e-4f, "the armature maps local +Y to model −Z (" + step + ")");
            var d = AnimationService.RootDelta(skel, clip, root, 0, 0.3f, 0.4f, false, true);
            t.True((d - step).Length() < 1e-4f, "0.1 s of the walk = 0.2 m in model space (" + d + " vs " + step + ")");
            // a forward wrap from 0.95 to 0.05: the end-to-start jump is skipped → still 0.2 m, not −1.8 m
            d = AnimationService.RootDelta(skel, clip, root, 0, 0.95f, 0.05f, true, true);
            t.True((d - step).Length() < 1e-4f, "the loop wrap keeps the travel continuous (" + d + ")");
            // playing backwards across the wrap: the opposite travel
            d = AnimationService.RootDelta(skel, clip, root, 0, 0.05f, 0.95f, true, false);
            t.True((d + step).Length() < 1e-4f, "reverse wrap travels backwards (" + d + ")");
            // an in-place clip (no hips track) has no travel
            var idle = new VortexAnimClip { Name = "idle", DurationSec = 1f, Loop = true };
            idle.Tracks.Add(new AnimTrack { Bone = "mixamorig:Spine", Pos = { new AnimKeyVec3 { T = 0, X = 0, Y = 0.2f, Z = 0 } } });
            var idleNodes = AnimationService.ResolveTrackNodes(skel, idle);
            int track = -1;
            for (int i = 0; i < idleNodes.Length; i++) if (idleNodes[i] == root) track = i;
            t.Equal(-1, track, "an in-place clip has no root track");
            t.True(AnimationService.RootDelta(skel, idle, root, track, 0.1f, 0.2f, false, true) == Vector3.Zero, "no track → zero delta");
            // an unrotated armature: the delta stays on the local axis
            var flat = Rig(rotatedArmature: false);
            d = AnimationService.RootDelta(flat, clip, 1, 0, 0f, 0.5f, false, true);
            t.True(Math.Abs(d.Y - 1f) < 1e-4f && Math.Abs(d.Z) < 1e-4f, "without the armature rotation the travel is +Y (" + d + ")");
        }

        [Test]
        public static void RootMotionBoneDetection(TestContext t)
        {
            t.Equal(1, AnimationService.FindRootMotionNode(Rig("Pelvis"), ""), "pelvis by name");
            t.Equal(1, AnimationService.FindRootMotionNode(Rig("Bip01_Hip"), ""), "hip by name");
            t.Equal(2, AnimationService.FindRootMotionNode(Rig(), "Spine"), "an explicit bone wins");
            t.Equal(2, AnimationService.FindRootMotionNode(Rig(), "mixamorig:Spine"), "explicit with namespace");
            // no recognisable name: the first skinned node under the model root
            var odd = Rig("Node_07");
            int n = AnimationService.FindRootMotionNode(odd, "");
            t.Equal(1, n, "falls back to the first skinned node under the root (" + n + ")");
            t.Equal(-1, AnimationService.FindRootMotionNode(new SkeletonDef(), ""), "an empty skeleton has no root bone");
        }
    }
}
