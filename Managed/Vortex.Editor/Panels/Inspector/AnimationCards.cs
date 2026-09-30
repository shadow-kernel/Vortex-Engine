using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Matrix4x4 = System.Numerics.Matrix4x4;
using Quaternion = System.Numerics.Quaternion;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Editor.Core.Animation;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.ECS;
using Editor.ECS.Components.Animation;
using VortexEditor.Controls;
using VortexEditor.Shell;
using VortexEditor.Shell.Animation;
using static VortexEditor.Panels.Inspector.PropertyRows;

namespace VortexEditor.Panels.Inspector
{
    /// <summary>Inspector cards of the animation components: Animator, Bone Attachment, Two-Bone IK.</summary>
    internal static class AnimationCards
    {
        private static Window Owner => EditorCommands.Window;
        private static Scene SceneOf(GameEntity e) => e?.Scene ?? ProjectData.Current?.ActiveScene;
        private static void MarkDirty(GameEntity e) { var s = SceneOf(e); if (s != null) s.IsDirty = true; }

        // =====================================================================================================
        // Animator — clip table (name -> .vanim) with add / remove / browse / preview / default, fill from the model,
        // default clip, play on start, speed, open the Keyframe Editor.
        // =====================================================================================================
        public static IEnumerable<Control> AnimatorRows(Animator an, GameEntity entity)
        {
            if (an.Clips == null) an.Clips = new List<AnimatorClipEntry>();
            var rows = new List<Control>();
            var owner = entity ?? an.Entity;
            SkeletonDef skel = null;
            try { AnimationService.Instance.TryGetNodeWorlds(owner, out skel, out _); } catch { }
            if (skel == null) rows.Add(Warning("No rigged model on this entity or its children — the Animator needs a skinned mesh (a model with bones) to play clips."));

            rows.Add(Section("Clips"));
            var table = new StackPanel { Spacing = 3 };
            var defaultBox = new ComboBox { MinHeight = 22, HorizontalAlignment = HorizontalAlignment.Stretch };
            bool syncing = false;
            Action rebuild = null;

            void SyncDefault()
            {
                syncing = true;
                defaultBox.Items.Clear();
                defaultBox.Items.Add("(none)");
                foreach (var c in an.Clips) if (!string.IsNullOrWhiteSpace(c.Name)) defaultBox.Items.Add(c.Name);
                string d = an.DefaultClip ?? "";
                if (d.Length > 0 && !an.Clips.Any(c => string.Equals(c.Name, d, StringComparison.OrdinalIgnoreCase))) defaultBox.Items.Add(d);
                int idx = 0;
                for (int i = 1; i < defaultBox.Items.Count; i++) if (string.Equals(defaultBox.Items[i] as string, d, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
                defaultBox.SelectedIndex = idx;
                syncing = false;
            }

            rebuild = () =>
            {
                table.Children.Clear();
                if (an.Clips.Count == 0)
                    table.Children.Add(new TextBlock { Text = "No clips yet — add one, or fill the table from the model's animations folder.", Classes = { "small", "tertiary" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2) });
                foreach (var entry in an.Clips.ToList())
                {
                    var e0 = entry;
                    var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,112,*,Auto,Auto,Auto") };
                    bool isDefault = !string.IsNullOrEmpty(an.DefaultClip) && string.Equals(an.DefaultClip, e0.Name, StringComparison.OrdinalIgnoreCase);
                    var star = new Button { Classes = { "icon", "small" }, Content = new TextBlock { Text = isDefault ? "★" : "☆", FontSize = 13, Foreground = isDefault ? AnimUi.Res("VxYellowBrush") : AnimUi.Res("VxTextTertiaryBrush") } };
                    ToolTip.SetTip(star, isDefault ? "Default clip (plays on start)" : "Make this the default clip");
                    star.Click += (s, e) => { an.DefaultClip = e0.Name; MarkDirty(owner); rebuild(); };
                    row.Children.Add(star);
                    var name = new TextBox { Text = e0.Name, MinHeight = 22, Watermark = "name" };
                    ToolTip.SetTip(name, "Clip name scripts use (Animation.Play(entity, \"name\"))");
                    void CommitName()
                    {
                        string nv = (name.Text ?? "").Trim();
                        if (nv == e0.Name) return;
                        bool wasDefault = string.Equals(an.DefaultClip, e0.Name, StringComparison.OrdinalIgnoreCase);
                        e0.Name = nv;
                        if (wasDefault) an.DefaultClip = nv;   // the default follows the rename
                        MarkDirty(owner); SyncDefault();
                    }
                    name.LostFocus += (s, e) => CommitName();
                    name.KeyDown += (s, e) => { if (e.Key == Key.Return) { CommitName(); e.Handled = true; } };
                    Grid.SetColumn(name, 1); row.Children.Add(name);
                    // the file name reads at a glance (the full project-relative path is the tooltip); click = browse,
                    // drop a clip, right-click = edit the raw path / reveal the file
                    bool missing = !string.IsNullOrEmpty(e0.Path) && !File.Exists(AnimUtil.ToAbsolute(e0.Path));
                    var pathText = new TextBlock
                    {
                        Text = string.IsNullOrEmpty(e0.Path) ? "choose a .vanim…" : Path.GetFileName(e0.Path) + (missing ? "  (missing)" : ""),
                        TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                        Foreground = missing ? AnimUi.Res("VxRedBrush") : string.IsNullOrEmpty(e0.Path) ? AnimUi.Res("VxTextTertiaryBrush") : AnimUi.Res("VxTextBrush")
                    };
                    var path = new Button { Content = pathText, MinHeight = 24, Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(8, 2), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
                    ToolTip.SetTip(path, (missing ? "File not found: " : "") + (string.IsNullOrEmpty(e0.Path) ? "Pick or drop a .vanim clip" : e0.Path) + "\nClick to choose another clip · right-click for more");
                    path.Click += async (s, e) => { var p = await AssetPickerDialog.Pick("Animations", ComponentEditors.AnimPatterns); if (!string.IsNullOrEmpty(p)) { SetClipPath(an, e0, p); MarkDirty(owner); rebuild(); } };
                    var pathMenu = new ContextMenu();
                    var editPath = new MenuItem { Header = "Edit path…" };
                    editPath.Click += async (s, e) =>
                    {
                        var nv = await AnimUi.Prompt(Owner, "Clip path", "Project-relative .vanim path", e0.Path, "Apply");
                        if (nv == null) return;
                        nv = nv.Trim().Replace('\\', '/');
                        if (nv == e0.Path) return;
                        e0.Path = nv; MarkDirty(owner); rebuild();
                    };
                    var reveal = new MenuItem { Header = "Reveal in Finder", IsEnabled = !missing && !string.IsNullOrEmpty(e0.Path) };
                    reveal.Click += (s, e) => EditorCommands.RevealInFinder(AnimUtil.ToAbsolute(e0.Path));
                    pathMenu.Items.Add(editPath); pathMenu.Items.Add(reveal);
                    path.ContextMenu = pathMenu;
                    DragDrop.SetAllowDrop(path, true);
                    path.AddHandler(DragDrop.DragOverEvent, (s, e) => { var p = DroppedPath(e, "vortex/asset"); e.DragEffects = p != null && Matches(p, ComponentEditors.AnimPatterns) ? DragDropEffects.Link : DragDropEffects.None; e.Handled = true; });
                    path.AddHandler(DragDrop.DropEvent, (s, e) => { var p = DroppedPath(e, "vortex/asset"); if (p != null && Matches(p, ComponentEditors.AnimPatterns)) { SetClipPath(an, e0, ToProjectRelative(p)); MarkDirty(owner); rebuild(); e.Handled = true; } });
                    Grid.SetColumn(path, 2); row.Children.Add(path);
                    var browse = new Button { Classes = { "icon", "small" }, Content = new VxIcon { Icon = "Folder", Width = 13, Height = 13 }, Margin = new Thickness(2, 0, 0, 0) };
                    ToolTip.SetTip(browse, "Browse for a .vanim clip");
                    browse.Click += async (s, e) => { var p = await AssetPickerDialog.Pick("Animations", ComponentEditors.AnimPatterns); if (!string.IsNullOrEmpty(p)) { SetClipPath(an, e0, p); MarkDirty(owner); rebuild(); } };
                    Grid.SetColumn(browse, 3); row.Children.Add(browse);
                    var preview = new Button { Classes = { "icon", "small" }, Content = new VxIcon { Icon = "Play", Width = 12, Height = 12 }, IsEnabled = !string.IsNullOrEmpty(e0.Path) && !missing };
                    ToolTip.SetTip(preview, "Preview / edit this clip in the Keyframe Editor");
                    preview.Click += (s, e) => AnimationEditorWindow.Open(AnimUtil.ToAbsolute(e0.Path));
                    Grid.SetColumn(preview, 4); row.Children.Add(preview);
                    var del = new Button { Classes = { "icon", "small" }, Content = new VxIcon { Icon = "Close", Width = 12, Height = 12 } };
                    ToolTip.SetTip(del, "Remove clip");
                    del.Click += (s, e) =>
                    {
                        an.Clips.Remove(e0);
                        if (string.Equals(an.DefaultClip, e0.Name, StringComparison.OrdinalIgnoreCase)) an.DefaultClip = an.Clips.FirstOrDefault()?.Name ?? "";
                        MarkDirty(owner); rebuild();
                    };
                    Grid.SetColumn(del, 5); row.Children.Add(del);
                    table.Children.Add(row);
                }
                SyncDefault();
            };
            rebuild();
            rows.Add(table);

            var add = new Button { Content = "+ Add Clip", Classes = { "ghost" } };
            add.Click += async (s, e) =>
            {
                // pick right away (cancel = an empty row to fill in later, like the Windows editor)
                var p = await AssetPickerDialog.Pick("Animations", ComponentEditors.AnimPatterns);
                var entry = new AnimatorClipEntry { Name = "Clip" + an.Clips.Count, Path = "" };
                if (!string.IsNullOrEmpty(p)) SetClipPath(an, entry, p);
                an.Clips.Add(entry);
                if (string.IsNullOrEmpty(an.DefaultClip) && !string.IsNullOrEmpty(entry.Path)) an.DefaultClip = entry.Name;
                MarkDirty(owner); rebuild();
            };
            var fromModel = new Button { Content = "Add clips from model", Classes = { "ghost" } };
            ToolTip.SetTip(fromModel, "Add every animations/*.vanim next to this entity's model (where the importer extracts clips)");
            fromModel.Click += async (s, e) =>
            {
                string meshPath = AnimUtil.FindMeshPath(owner);
                if (meshPath == null || !AnimationService.TryPopulateClipsFromModel(an, meshPath))
                {
                    await AnimUi.Alert(Owner, "Animator", "No animations/*.vanim found next to this entity's model.");
                    return;
                }
                MarkDirty(owner); rebuild(); RefreshAll();
            };
            rows.Add(Actions(add, fromModel));

            rows.Add(Section("Playback"));
            defaultBox.SelectionChanged += (s, e) =>
            {
                if (syncing || defaultBox.SelectedIndex < 0) return;
                string v = defaultBox.SelectedIndex == 0 ? "" : defaultBox.SelectedItem as string ?? "";
                if (v != (an.DefaultClip ?? "")) { an.DefaultClip = v; MarkDirty(owner); rebuild(); }
            };
            Refreshers[defaultBox] = () => rebuild();
            rows.Add(Row("Default clip", defaultBox, "Played on start (by name from the table, or a .vanim path)"));
            rows.Add(Row("Play on start", Bool(() => an.PlayOnStart, v => an.PlayOnStart = v)));
            rows.Add(Row("Speed", SliderRow(() => an.Speed, v => an.Speed = v, 0, 3, "0.##", 0f, 10f), "Playback rate multiplier (1 = authored speed)"));

            var open = new Button { Content = "Open Keyframe Editor…", Classes = { "ghost" } };
            open.Click += async (s, e) =>
            {
                string path = an.ResolveClipPath(an.DefaultClip);
                if (string.IsNullOrEmpty(path)) path = an.Clips.FirstOrDefault(c => !string.IsNullOrEmpty(c.Path))?.Path;
                if (string.IsNullOrEmpty(path)) { await AnimUi.Alert(Owner, "Keyframe Editor", "Add a clip (.vanim) to this Animator first."); return; }
                AnimationEditorWindow.Open(AnimUtil.ToAbsolute(path));
            };
            rows.Add(Actions(open));
            return rows;
        }

        private static void SetClipPath(Animator an, AnimatorClipEntry entry, string path)
        {
            string rel = ToProjectRelative(path)?.Replace('\\', '/');
            entry.Path = rel;
            if (string.IsNullOrWhiteSpace(entry.Name) || entry.Name.StartsWith("Clip", StringComparison.Ordinal))
            {
                string oldName = entry.Name;
                entry.Name = Path.GetFileNameWithoutExtension(rel);
                if (!string.IsNullOrEmpty(oldName) && string.Equals(an.DefaultClip, oldName, StringComparison.OrdinalIgnoreCase)) an.DefaultClip = entry.Name;
            }
        }

        // =====================================================================================================
        // Bone Attachment — target (by entity, or the nearest Animator ancestor), bone picked from the target's skeleton,
        // socket prefab + its render layer, bone-space offset, Snap to Bone / Capture Offset / Socket Editor.
        // =====================================================================================================
        private const string NearestToken = "@nearest";

        public static IEnumerable<Control> BoneAttachmentRows(BoneAttachment ba, GameEntity entity)
        {
            var rows = new List<Control>();
            var owner = entity ?? ba.Entity;
            var info = new TextBlock { Classes = { "small", "secondary" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(LabelWidth + 10, 0, 0, 4) };
            void RefreshInfo()
            {
                GameEntity target = null;
                try { target = BoneSocketService.Instance.ResolveTargetOf(SceneOf(owner), owner); } catch { }
                bool boneOk = false;
                if (target != null && !string.IsNullOrEmpty(ba.BoneName))
                    try { boneOk = BoneSocketService.Instance.GetBoneNamesFor(SceneOf(owner), owner).Contains(ba.BoneName); } catch { }
                info.Text = target == null ? "No target resolves — pick an animated character, or parent this entity under one with an Animator."
                          : "Resolves to: " + target.Name + (string.IsNullOrEmpty(ba.BoneName) ? " — pick a bone." : boneOk ? "" : " — bone \"" + ba.BoneName + "\" is not on its skeleton.");
                info.Foreground = target == null || (!string.IsNullOrEmpty(ba.BoneName) && !boneOk) ? AnimUi.Res("VxOrangeBrush") : AnimUi.Res("VxTextSecondaryBrush");
            }

            rows.Add(Section("Target"));
            var targetPicker = SearchPicker(
                () => string.IsNullOrEmpty(ba.TargetEntityId) ? NearestToken : ba.TargetEntityId,
                v => { ba.TargetEntityId = v == NearestToken ? "" : v; RefreshInfo(); RefreshAll(); },
                () => new[] { NearestToken }.Concat(AnimatorEntities(owner).Select(a => a.Id.ToString())),
                "Nearest parent with an Animator",
                v => v == NearestToken ? "Nearest parent with an Animator" : EntityLabel(owner, v),
                false, "The animated character this entity follows");
            rows.Add(Row("Character", targetPicker));
            rows.Add(Row("Target id", Text(() => ba.TargetEntityId, v => { ba.TargetEntityId = (v ?? "").Trim(); RefreshInfo(); RefreshAll(); }, "empty = nearest parent with an Animator"),
                "Entity GUID of the skeletal target. Leave EMPTY to use the nearest parent with an Animator (the usual case: weapon as child of the character)."));
            rows.Add(info);

            rows.Add(Section("Bone"));
            rows.Add(Row("Bone", SearchPicker(() => ba.BoneName, v => { ba.BoneName = v; RefreshInfo(); }, () =>
            {
                string[] names = Array.Empty<string>();
                try { names = BoneSocketService.Instance.GetBoneNamesFor(SceneOf(owner), owner); } catch { }
                return names.Where(n => !AnimUtil.IsHiddenNode(n));
            }, "Pick a bone", AnimUtil.DisplayBoneName, true, "Skeleton bone this entity follows (from the target's skeleton). Type to filter.")));

            rows.Add(Section("Socket prefab (attached at play)"));
            rows.Add(Row("Prefab", AssetPath(() => ba.SocketPrefabPath, v => ba.SocketPrefabPath = v ?? "", "Prefab", ComponentEditors.PrefabPatterns, () => AssetPickerDialog.Pick("Prefabs", ComponentEditors.PrefabPatterns)),
                "The prefab (.ventity) spawned + attached to this bone when the scene starts. Empty = this entity IS the attachment."));
            rows.Add(Row("Render layer", Choice(() => ba.SocketRenderLayer + 1, v => ba.SocketRenderLayer = v - 1, "Keep prefab", "World", "First-Person (viewmodel)", "Third-Person only"),
                "Forced onto every mesh of the SPAWNED prefab at play start. 'Keep prefab' uses what the prefab authored. A weapon socketed to a Third-Person-only body needs 'Third-Person only' here."));

            rows.Add(Section("Offset (bone space)"));
            rows.Add(Row("Position", Vector3(() => ba.OffsetPosition, v => ba.OffsetPosition = v, 0.01)));
            rows.Add(Row("Rotation", Vector3(() => ba.OffsetRotation, v => ba.OffsetRotation = v, 1)));
            rows.Add(Row("Scale", Vector3(() => ba.OffsetScale, v => ba.OffsetScale = v, 0.05, 0.001f)));

            var snap = Ghost("Snap to Bone", async () =>
            {
                bool ok = false;
                try { ok = BoneSocketService.Instance.ApplyOne(SceneOf(owner), owner); } catch { }
                SceneRenderService.RuntimeDirty = true;
                if (!ok) await AnimUi.Alert(Owner, "Bone Attachment", string.IsNullOrEmpty(ba.SocketPrefabPath) ? "Could not resolve the bone — check the target and bone name." : "This socket spawns its prefab at play start, so the placeholder itself doesn't move. Open the Socket Editor to preview the attachment.");
                RefreshAll();
            }, "Apply the socket now: move this entity onto the bone (bind pose in edit mode) so you see the result in the viewport.");
            var capture = Ghost("Capture Offset", async () =>
            {
                bool ok = false;
                try { ok = BoneSocketService.Instance.CaptureOffsetFromCurrentPose(SceneOf(owner), owner); } catch { }
                if (ok) RefreshAll();
                else await AnimUi.Alert(Owner, "Bone Attachment", "Could not resolve the bone — check the target and bone name.");
            }, "Bake this entity's CURRENT transform into the offsets: drag it into place with the normal gizmo first, then click here.");
            var editor = Ghost("Open Socket Editor…", () => SocketEditorWindow.Open(owner),
                "Position the attachment on the bone with a LIVE posed 3D preview + nudge keys, then Save — writes bone + offset back to this component.");
            rows.Add(Actions(snap, capture, editor));
            rows.Add(Hint("In play mode the socket drives this entity every frame. In edit mode use Snap / Capture; the normal transform gizmo stays free for placing."));
            RefreshInfo();
            Refreshers[info] = RefreshInfo;
            return rows;
        }

        /// <summary>Scene entities that carry an Animator (socket targets), the attachment itself excluded.</summary>
        private static IEnumerable<GameEntity> AnimatorEntities(GameEntity self)
            => AnimUtil.AllEntities(SceneOf(self)).Where(e => !ReferenceEquals(e, self) && e.GetComponent<Animator>() != null);

        private static string EntityLabel(GameEntity self, string id)
        {
            var e = AnimUtil.AllEntities(SceneOf(self)).FirstOrDefault(x => x.Id.ToString() == id);
            if (e == null) return "(missing entity " + (id.Length > 8 ? id.Substring(0, 8) + "…" : id) + ")";
            return e.Parent != null ? e.Name + "  ·  " + e.Parent.Name : e.Name;
        }

        // =====================================================================================================
        // Two-Bone IK — tip / target bones from the entity's own skeleton, grip offset, weight / pole sliders, tip
        // rotation, auto-grip, capture the grip from the current pose; edits re-pose the viewport live.
        // =====================================================================================================
        public static IEnumerable<Control> TwoBoneIkRows(TwoBoneIk ik, GameEntity entity)
        {
            var rows = new List<Control>();
            var owner = entity ?? ik.Entity;
            var chain = new TextBlock { Classes = { "small", "secondary" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 4) };
            void RefreshChain()
            {
                string text = "No skeleton resolves — put this component on the entity that carries the Animator.";
                bool warn = true;
                if (AnimationService.Instance.TryGetNodeWorlds(owner, out var skel, out _))
                {
                    int tip = string.IsNullOrEmpty(ik.TipBone) ? -1 : skel.FindNode(ik.TipBone);
                    if (tip >= 0)
                    {
                        int mid = skel.Nodes[tip].Parent;
                        int root = mid >= 0 ? skel.Nodes[mid].Parent : -1;
                        if (root >= 0)
                        {
                            text = "Chain: " + AnimUtil.DisplayBoneName(skel.Nodes[root].Name) + " → " + AnimUtil.DisplayBoneName(skel.Nodes[mid].Name) + " → " + AnimUtil.DisplayBoneName(skel.Nodes[tip].Name);
                            warn = !string.IsNullOrEmpty(ik.TargetBone) && skel.FindNode(ik.TargetBone) < 0;
                            if (warn) text += "  —  target bone \"" + ik.TargetBone + "\" is not on this skeleton.";
                            else if (string.IsNullOrEmpty(ik.TargetBone)) { text += "  —  pick the target bone."; warn = true; }
                        }
                        else text = "Tip bone has no grandparent — a two-bone chain needs 3 joints.";
                    }
                    else text = string.IsNullOrEmpty(ik.TipBone) ? "Pick the tip bone (the hand that should grip)." : "Tip bone \"" + ik.TipBone + "\" is not on this skeleton.";
                }
                chain.Text = text;
                chain.Foreground = warn ? AnimUi.Res("VxOrangeBrush") : AnimUi.Res("VxTextSecondaryBrush");
            }
            void AfterEdit() { RefreshChain(); SceneRenderService.RuntimeDirty = true; }
            IEnumerable<string> OwnBones()
            {
                if (!AnimationService.Instance.TryGetNodeWorlds(owner, out var skel, out _)) return Enumerable.Empty<string>();
                return skel.Nodes.Select(n => n.Name).Where(n => !string.IsNullOrEmpty(n) && !AnimUtil.IsHiddenNode(n));
            }

            rows.Add(chain);
            rows.Add(Section("Chain"));
            rows.Add(Row("Tip bone", SearchPicker(() => ik.TipBone, v => { ik.TipBone = v; AfterEdit(); }, OwnBones, "Pick the reaching bone", AnimUtil.DisplayBoneName, true,
                "The reaching bone (e.g. mixamorig:LeftHand). Mid / root joints = its parent and grandparent.")));
            rows.Add(Row("Target bone", SearchPicker(() => ik.TargetBone, v => { ik.TargetBone = v; AfterEdit(); }, OwnBones, "Pick the grip bone", AnimUtil.DisplayBoneName, true,
                "The bone the grip is expressed against (e.g. mixamorig:RightHand — the weapon hand).")));

            rows.Add(Section("Grip offset (target-bone space, model units)"));
            rows.Add(Row("Position", Vector3(() => ik.TargetOffsetPosition, v => { ik.TargetOffsetPosition = v; AfterEdit(); }, 0.5), "Model units (cm on a Mixamo rig)"));
            rows.Add(Row("Rotation", Vector3(() => ik.TargetOffsetRotation, v => { ik.TargetOffsetRotation = v; AfterEdit(); }, 1), "Euler degrees (ZXY) — the wrist takes this orientation with 'Apply tip rotation'"));

            rows.Add(Section("Solve"));
            rows.Add(Row("Weight", SliderRow(() => ik.Weight, v => { ik.Weight = v; AfterEdit(); }, 0, 1), "0 = animation only, 1 = full IK. Scripts blend it at runtime via Animation.SetIkWeight."));
            rows.Add(Row("Pole angle", SliderRow(() => ik.PoleAngle, v => { ik.PoleAngle = v; AfterEdit(); }, -180, 180, "0"), "Rotates the elbow around the shoulder-to-target axis (degrees). 0 keeps the animation's natural bend."));
            rows.Add(Row("", Check("Apply tip rotation (orient the wrist to the grip)", () => ik.ApplyTipRotation, v => { ik.ApplyTipRotation = v; AfterEdit(); })));
            rows.Add(Row("", Check("Auto-grip (capture the natural hold from the animation)", () => ik.AutoGrip, v => { ik.AutoGrip = v; AfterEdit(); },
                "On: the support hand locks to wherever the idle / hold animation puts it relative to the weapon hand, through every clip (the offset fields fine-tune on top). Off: only the explicit grip offset.")));

            var capture = Ghost("Capture From Current Pose", async () =>
            {
                if (string.IsNullOrEmpty(ik.TipBone) || string.IsNullOrEmpty(ik.TargetBone)) { await AnimUi.Alert(Owner, "Two-Bone IK", "Set Tip Bone and Target Bone first."); return; }
                if (!CaptureGrip(ik, owner)) await AnimUi.Alert(Owner, "Two-Bone IK", "Could not resolve the bones — check Tip / Target names against the skeleton.");
                RefreshAll(); AfterEdit();
            }, "Bake the CURRENT tip-to-target relation into the grip offset — pose the character first (e.g. a rifle-hold frame), then click. Captured from the un-IK'd pose (weight is temporarily zeroed).");
            rows.Add(Actions(capture));
            rows.Add(Hint("The hand follows the grip point through EVERY animation once the weapon is socketed to the target bone. Offsets are in model units (cm on a Mixamo rig). The viewport previews the IK'd pose live while you tune."));
            RefreshChain();
            Refreshers[chain] = RefreshChain;
            return rows;
        }

        /// <summary>Capture the grip from the UN-IK'd pose: zero the weight, re-pose, read tip-relative-to-target, restore.</summary>
        internal static bool CaptureGrip(TwoBoneIk ik, GameEntity owner)
        {
            float saved = ik.Weight;
            var svc = AnimationService.Instance;
            bool ok = false;
            try
            {
                ik.Weight = 0f;
                svc.RefreshIk(owner);
                if (svc.TryGetNodeWorlds(owner, out var skel, out var worlds) && worlds != null)
                {
                    int tip = skel.FindNode(ik.TipBone), tgt = skel.FindNode(ik.TargetBone);
                    if (tip >= 0 && tgt >= 0 && tip < worlds.Length && tgt < worlds.Length && Matrix4x4.Invert(worlds[tgt], out var inv))
                    {
                        var off = worlds[tip] * inv;
                        if (!Matrix4x4.Decompose(off, out _, out var q, out var tr)) { tr = off.Translation; q = Quaternion.Identity; }
                        var euler = BoneSocketService.ToEulerZXY(Matrix4x4.CreateFromQuaternion(q));
                        ik.TargetOffsetPosition = new Editor.ECS.Vector3(tr.X, tr.Y, tr.Z);
                        ik.TargetOffsetRotation = new Editor.ECS.Vector3(euler.X, euler.Y, euler.Z);
                        ok = true;
                    }
                }
            }
            catch { }
            finally { ik.Weight = saved; }
            return ok;
        }
    }
}
