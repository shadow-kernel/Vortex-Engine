using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Editor.Core.Animation
{
    /// <summary>
    /// Generic skeleton analysis for the rig-aware animation components (HandPose, LookAtIk, FootIk and the TwoBoneIk /
    /// FootIk limb derivation). Works on ANY imported skeleton, not one naming scheme:
    ///  - bone SIDES are read from every common convention — Mixamo "LeftHand", Unreal "hand_l", Rigify "DEF-hand.L",
    ///    Unity/VRM "Left Hand" / "J_Bip_L_Hand", 3ds Max Biped "Bip01 L Hand", DAZ "lHand", Character Creator
    ///    "CC_Base_L_Hand" (names are split into tokens at separators, camelCase and digit boundaries);
    ///  - hands, fingers, head/neck/spine and legs are found by name KEYWORDS, with bind-pose GEOMETRY as the fallback
    ///    (unnamed finger chains are sorted around the palm, the thumb is the chain whose direction deviates);
    ///  - joints that cannot move a skinned vertex (end markers like "Index4"/"_end"/"Nub", unweighted helpers) are
    ///    filtered out, metacarpal/palm segments are recognised (by name or because they start inside the palm).
    /// Model space is the importer's right-handed space (Assimp, no handedness conversion): left × up = forward, which
    /// is what makes the palm side of a hand derivable from the hand's side label (plus an anatomical thumb check).
    /// Everything here is pure bind-pose analysis (cached per skeleton); the runtime application lives in
    /// <see cref="AnimationService"/>.
    /// </summary>
    public static class RigMap
    {
        public const int Index = 0, Middle = 1, Ring = 2, Pinky = 3, Thumb = 4;

        /// <summary>Finger slot names in the order HandPose uses (index, middle, ring, pinky, thumb).</summary>
        public static readonly string[] FingerNames = { "Index", "Middle", "Ring", "Pinky", "Thumb" };

        // ------------------------------------------------------------------ per-skeleton cache

        /// <summary>Bind-pose facts about one skeleton (built once, cached per <see cref="SkeletonDef"/>).</summary>
        public sealed class SkelInfo
        {
            public SkeletonDef Skel;
            public Matrix4x4[] Bind;          // bind node worlds (model space)
            public List<int>[] Children;
            public int[] Depth;
            public bool[] Skinned;            // node is a palette bone
            public bool[] Effective;          // node or a descendant is a palette bone (rotating it moves vertices)
            public int[] SubtreeDepth;        // nodes on the longest path from the node down to a leaf (leaf = 1)
            public string[] Lower;            // lower-case names
            public string[][] Tokens;         // lower-case name tokens
            public int[] Side;                // -1 left, +1 right, 0 = the name carries no side
            internal Frame FrameCache;
            internal int HeadCache = -2;
        }

        /// <summary>Cached <see cref="FindHead"/> (look-at targets query other characters' heads every frame).</summary>
        public static int HeadOf(SkelInfo si)
        {
            if (si == null) return -1;
            if (si.HeadCache == -2) si.HeadCache = FindHead(si, null);
            return si.HeadCache;
        }

        private static readonly ConditionalWeakTable<SkeletonDef, SkelInfo> _cache = new ConditionalWeakTable<SkeletonDef, SkelInfo>();

        /// <summary>Analysis of a skeleton (cached; null for null).</summary>
        public static SkelInfo Info(SkeletonDef skel)
        {
            if (skel == null || skel.Nodes == null) return null;
            return _cache.GetValue(skel, BuildInfo);
        }

        private static SkelInfo BuildInfo(SkeletonDef skel)
        {
            int n = skel.Nodes.Length;
            var si = new SkelInfo
            {
                Skel = skel,
                Bind = skel.BindNodeWorlds(),
                Children = new List<int>[n],
                Depth = new int[n],
                Skinned = new bool[n],
                Effective = new bool[n],
                SubtreeDepth = new int[n],
                Lower = new string[n],
                Tokens = new string[n][],
                Side = new int[n],
            };
            for (int i = 0; i < n; i++) si.Children[i] = new List<int>();
            for (int i = 0; i < n; i++)
            {
                int p = skel.Nodes[i].Parent;
                if (p >= 0 && p < n && p != i) si.Children[p].Add(i);
            }
            for (int i = 0; i < n; i++)
            {
                int d = 0;
                for (int p = skel.Nodes[i].Parent; p >= 0 && p < n && d <= n; p = skel.Nodes[p].Parent) d++;
                si.Depth[i] = d;
            }
            if (skel.Bones != null)
                foreach (var b in skel.Bones)
                    if (b != null && b.NodeIndex >= 0 && b.NodeIndex < n) si.Skinned[b.NodeIndex] = true;

            // Bottom-up pass (deepest first) — does not rely on the parents-before-children node order.
            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            Array.Sort(order, (a, b) => si.Depth[b].CompareTo(si.Depth[a]));
            foreach (int i in order)
            {
                int sd = 1; bool eff = si.Skinned[i];
                foreach (int c in si.Children[i])
                {
                    if (si.SubtreeDepth[c] + 1 > sd) sd = si.SubtreeDepth[c] + 1;
                    if (si.Effective[c]) eff = true;
                }
                si.SubtreeDepth[i] = sd;
                si.Effective[i] = eff;
            }
            for (int i = 0; i < n; i++)
            {
                string name = skel.Nodes[i].Name ?? "";
                si.Lower[i] = name.ToLowerInvariant();
                si.Tokens[i] = Tokens(name);
                si.Side[i] = SideOfTokens(si.Tokens[i], si.Lower[i]);
            }
            return si;
        }

        public static Vector3 Pos(SkelInfo si, int node) => si.Bind[node].Translation;

        // ------------------------------------------------------------------ names

        private struct Span { public int Start, Len; }

        private static List<Span> Spans(string s)
        {
            var list = new List<Span>();
            if (string.IsNullOrEmpty(s)) return list;
            int i = 0, n = s.Length;
            while (i < n)
            {
                if (!char.IsLetterOrDigit(s[i])) { i++; continue; }
                int start = i++;
                while (i < n && char.IsLetterOrDigit(s[i]))
                {
                    char prev = s[i - 1], c = s[i];
                    bool boundary =
                        (char.IsLower(prev) && char.IsUpper(c)) ||                                   // camelCase: lHand
                        (char.IsLetter(prev) && char.IsDigit(c)) ||                                  // Index1
                        (char.IsDigit(prev) && char.IsLetter(c)) ||                                  // 01L
                        (char.IsUpper(prev) && char.IsUpper(c) && i + 1 < n && char.IsLower(s[i + 1]));   // LHand -> L|Hand
                    if (boundary) break;
                    i++;
                }
                list.Add(new Span { Start = start, Len = i - start });
            }
            return list;
        }

        /// <summary>Lower-case name tokens: separators, camelCase and letter/digit boundaries split
        /// ("mixamorig:LeftHandIndex1" → mixamorig|left|hand|index|1, "DEF-f_index.01.L" → def|f|index|01|l).</summary>
        public static string[] Tokens(string name)
        {
            var spans = Spans(name);
            var t = new string[spans.Count];
            for (int i = 0; i < spans.Count; i++) t[i] = name.Substring(spans[i].Start, spans[i].Len).ToLowerInvariant();
            return t;
        }

        private static bool IsLeftToken(string t) => t == "left" || t == "l" || t == "lft" || t == "lt";
        private static bool IsRightToken(string t) => t == "right" || t == "r" || t == "rgt" || t == "rt";

        private static int SideOfTokens(string[] tokens, string lower)
        {
            bool l = false, r = false;
            foreach (var t in tokens)
            {
                if (IsLeftToken(t)) l = true;
                else if (IsRightToken(t)) r = true;
            }
            if (l && !r) return -1;
            if (r && !l) return 1;
            if (!l && !r)
            {
                // "Lefthand" / "handright" style names without a token boundary.
                bool sl = lower.StartsWith("left", StringComparison.Ordinal) || lower.EndsWith("left", StringComparison.Ordinal);
                bool sr = lower.StartsWith("right", StringComparison.Ordinal) || lower.EndsWith("right", StringComparison.Ordinal);
                if (sl && !sr) return -1;
                if (sr && !sl) return 1;
            }
            return 0;
        }

        /// <summary>Side a bone name denotes: -1 left, +1 right, 0 none.</summary>
        public static int SideOf(string name) => SideOfTokens(Tokens(name ?? ""), (name ?? "").ToLowerInvariant());

        /// <summary>The same bone name on the other side, in the name's own convention and letter case
        /// ("mixamorig:LeftHandIndex1" ↔ "mixamorig:RightHandIndex1", "hand_l" ↔ "hand_r", "DEF-f_index.01.L" ↔
        /// "DEF-f_index.01.R", "Bip01 L Hand" ↔ "Bip01 R Hand"). Names without a side token come back unchanged.</summary>
        public static string MirrorName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            var spans = Spans(name);
            var sb = new StringBuilder(name);
            for (int i = spans.Count - 1; i >= 0; i--)
            {
                string tok = name.Substring(spans[i].Start, spans[i].Len);
                string low = tok.ToLowerInvariant(), rep = null;
                switch (low)
                {
                    case "left": rep = "right"; break;
                    case "right": rep = "left"; break;
                    case "l": rep = "r"; break;
                    case "r": rep = "l"; break;
                    case "lft": rep = "rgt"; break;
                    case "rgt": rep = "lft"; break;
                    case "lt": rep = "rt"; break;
                    case "rt": rep = "lt"; break;
                }
                if (rep == null) continue;
                if (tok.ToUpperInvariant() == tok) rep = rep.ToUpperInvariant();
                else if (char.IsUpper(tok[0])) rep = char.ToUpperInvariant(rep[0]) + rep.Substring(1);
                sb.Remove(spans[i].Start, spans[i].Len);
                sb.Insert(spans[i].Start, rep);
            }
            return sb.ToString();
        }

        private static bool HasToken(SkelInfo si, int node, params string[] words)
        {
            var toks = si.Tokens[node];
            for (int i = 0; i < toks.Length; i++)
                for (int w = 0; w < words.Length; w++)
                    if (toks[i] == words[w]) return true;
            return false;
        }

        private static bool Contains(string s, string sub) => s.IndexOf(sub, StringComparison.Ordinal) >= 0;

        /// <summary>Rig helpers that are never part of a limb/finger/head chain (IK/FK controls, twist/roll
        /// helpers, sockets and weapon/prop attachment bones, Rigify MCH/tweak bones).</summary>
        public static bool IsHelper(SkelInfo si, int node)
        {
            return HasToken(si, node, "ik", "fk", "mch", "twist", "roll", "socket", "attach", "attachment", "weapon",
                "prop", "target", "pole", "ctrl", "control", "helper", "tweak", "vis", "dummy", "slot", "gun");
        }

        private static bool IsEndName(SkelInfo si, int node)
            => HasToken(si, node, "end", "nub", "tip", "top", "null") || si.Lower[node].EndsWith("_end", StringComparison.Ordinal);

        private static bool IsDefPrefixed(SkelInfo si, int node)
            => si.Lower[node].StartsWith("def-", StringComparison.Ordinal) || si.Lower[node].StartsWith("def_", StringComparison.Ordinal);

        private static bool IsOrgPrefixed(SkelInfo si, int node)
            => si.Lower[node].StartsWith("org-", StringComparison.Ordinal) || si.Lower[node].StartsWith("org_", StringComparison.Ordinal);

        /// <summary>Finger slot a bone name denotes (0 index, 1 middle, 2 ring, 3 pinky, 4 thumb) or -1.
        /// Covers thumb/index/middle/ring/pinky/little (Unity, VRM), mid (DAZ, CC), and Biped "Finger0..4".</summary>
        public static int FingerOfName(SkelInfo si, int node)
        {
            string lw = si.Lower[node];
            var toks = si.Tokens[node];
            if (Contains(lw, "thumb") || Contains(lw, "pollex")) return Thumb;
            if (Contains(lw, "index") || Contains(lw, "pointer") || Contains(lw, "forefinger")) return Index;
            if (Contains(lw, "middle")) return Middle;
            if (Contains(lw, "pinky") || Contains(lw, "pinkie") || Contains(lw, "little")) return Pinky;
            if (Contains(lw, "ring")) return Ring;
            for (int i = 0; i < toks.Length; i++)
            {
                if (toks[i] == "mid") return Middle;
                if (toks[i] == "small" || toks[i] == "pink") return Pinky;   // "L_pink1" (common FPS-arms rigs)
                if (toks[i] == "point") return Index;                         // "L_point1"
            }
            // 3ds Max Biped: "Finger0" = thumb ... "Finger4" = pinky ("Finger01" = finger 0, joint 1).
            for (int i = 0; i + 1 < toks.Length; i++)
            {
                if (toks[i] != "finger") continue;
                string d = toks[i + 1];
                if (d.Length == 0 || !char.IsDigit(d[0])) continue;
                switch (d[0])
                {
                    case '0': return Thumb;
                    case '1': return Index;
                    case '2': return Middle;
                    case '3': return Ring;
                    case '4': return Pinky;
                }
            }
            return -1;
        }

        /// <summary>Find a bone by name: exact, then case-insensitive, then ignoring a namespace prefix
        /// ("LeftHand" finds "mixamorig:LeftHand", "mixamorig:Head" finds "mixamorig1:Head"). -1 when absent.</summary>
        public static int Find(SkeletonDef skel, string name)
        {
            if (skel == null || string.IsNullOrEmpty(name)) return -1;
            string want = name.Trim();
            int idx = skel.FindNode(want);
            if (idx >= 0) return idx;
            for (int i = 0; i < skel.Nodes.Length; i++)
                if (string.Equals(skel.Nodes[i].Name, want, StringComparison.OrdinalIgnoreCase)) return i;
            string bare = StripNamespace(want);
            for (int i = 0; i < skel.Nodes.Length; i++)
                if (string.Equals(StripNamespace(skel.Nodes[i].Name ?? ""), bare, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        private static string StripNamespace(string n)
        {
            int c = n.LastIndexOf(':');
            return c >= 0 ? n.Substring(c + 1) : n;
        }

        /// <summary>Comma/semicolon separated bone list → node indices (unknown names are reported into notes).</summary>
        public static int[] FindList(SkeletonDef skel, string list, List<string> notes, string what)
        {
            var result = new List<int>();
            if (skel == null || string.IsNullOrWhiteSpace(list)) return result.ToArray();
            foreach (var raw in list.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string nm = raw.Trim();
                if (nm.Length == 0) continue;
                int idx = Find(skel, nm);
                if (idx >= 0) { if (!result.Contains(idx)) result.Add(idx); }
                else if (notes != null) notes.Add(what + ": bone '" + nm + "' not found");
            }
            return result.ToArray();
        }

        public static string NameOf(SkelInfo si, int node) => node >= 0 && node < si.Skel.Nodes.Length ? si.Skel.Nodes[node].Name : "-";

        public static string NamesOf(SkelInfo si, int[] nodes)
        {
            if (nodes == null || nodes.Length == 0) return "-";
            var sb = new StringBuilder();
            for (int i = 0; i < nodes.Length; i++) { if (i > 0) sb.Append(", "); sb.Append(NameOf(si, nodes[i])); }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ hierarchy helpers

        public static int Parent(SkelInfo si, int node) => node >= 0 ? si.Skel.Nodes[node].Parent : -1;

        public static bool IsAncestor(SkelInfo si, int ancestor, int node)
        {
            for (int p = Parent(si, node); p >= 0; p = Parent(si, p)) if (p == ancestor) return true;
            return false;
        }

        /// <summary>Lowest common ancestor of two nodes (-1 if they are in different trees).</summary>
        public static int Lca(SkelInfo si, int a, int b)
        {
            if (a < 0 || b < 0) return -1;
            var seen = new HashSet<int>();
            for (int p = a; p >= 0; p = Parent(si, p)) seen.Add(p);
            for (int p = b; p >= 0; p = Parent(si, p)) if (seen.Contains(p)) return p;
            return -1;
        }

        /// <summary>A parent that only continues its own parent's segment — a twist/roll helper IN the chain, or a
        /// split segment ("DEF-forearm.L.001" under "DEF-forearm.L"). Limb joints skip these.</summary>
        private static bool IsSegmentContinuation(SkelInfo si, int node)
        {
            if (node < 0) return false;
            if (HasToken(si, node, "twist", "roll")) return true;
            int p = Parent(si, node);
            if (p < 0) return false;
            string a = si.Skel.Nodes[node].Name ?? "", b = si.Skel.Nodes[p].Name ?? "";
            if (a.Length <= b.Length || !a.StartsWith(b, StringComparison.Ordinal)) return false;
            string rest = a.Substring(b.Length);                      // ".001", "_001", ".1"
            if (rest.Length < 2 || (rest[0] != '.' && rest[0] != '_')) return false;
            for (int i = 1; i < rest.Length; i++) if (!char.IsDigit(rest[i])) return false;
            return true;
        }

        private static int JointAbove(SkelInfo si, int node)
        {
            int p = Parent(si, node);
            int guard = 0;
            while (p >= 0 && IsSegmentContinuation(si, p) && guard++ < 8) p = Parent(si, p);
            return p;
        }

        /// <summary>Two-bone limb for a tip joint (hand, foot): mid = the joint above it, root = the joint above that —
        /// skipping in-chain twist helpers and split segments, so Rigify "DEF-forearm.L.001" resolves to the elbow
        /// "DEF-forearm.L". Plain rigs (Mixamo, Unreal) resolve to parent / grandparent exactly as before.</summary>
        public static bool ResolveLimb(SkelInfo si, int tip, out int mid, out int root)
        {
            mid = root = -1;
            if (si == null || tip < 0) return false;
            mid = JointAbove(si, tip);
            root = mid >= 0 ? JointAbove(si, mid) : -1;
            return mid >= 0 && root >= 0;
        }

        // ------------------------------------------------------------------ model frame

        /// <summary>The character's bind-pose axes in model space.</summary>
        public sealed class Frame
        {
            public Vector3 Up = Vector3.UnitY, Forward = Vector3.UnitZ, Left = Vector3.UnitX;
            public string Source = "default (+Y up, +Z forward)";
        }

        /// <summary>Up / forward / left of the character in model space, from the bind pose: up = pelvis → head,
        /// forward = heel → toes (feet), else from the shoulders (left × up = forward), else +Z.</summary>
        public static Frame ModelFrame(SkelInfo si)
        {
            if (si == null) return new Frame();
            if (si.FrameCache != null) return si.FrameCache;
            var f = new Frame();
            // Name-only lookups here: the geometric fallbacks of FindHand/FindHead use this frame themselves.
            int head = FindHeadNamed(si);
            int footL = FindFoot(si, -1), footR = FindFoot(si, 1);
            int hipL = -1, hipR = -1, midL, midR;
            if (footL >= 0) ResolveLimb(si, footL, out midL, out hipL);
            if (footR >= 0) ResolveLimb(si, footR, out midR, out hipR);
            int pelvis = (hipL >= 0 && hipR >= 0) ? Lca(si, hipL, hipR) : -1;

            Vector3 up = Vector3.UnitY; bool haveUp = false;
            if (head >= 0 && pelvis >= 0)
            {
                var d = Pos(si, head) - Pos(si, pelvis);
                if (d.LengthSquared() > 1e-10f) { up = Vector3.Normalize(d); haveUp = true; }
            }
            if (!haveUp && head >= 0 && footL >= 0 && footR >= 0)
            {
                var d = Pos(si, head) - (Pos(si, footL) + Pos(si, footR)) * 0.5f;
                if (d.LengthSquared() > 1e-10f) { up = Vector3.Normalize(d); haveUp = true; }
            }
            // Snap a nearly axis-aligned up to the axis (bind poses lean a little).
            up = SnapAxis(up, 0.94f);

            Vector3 fwd = Vector3.Zero; string src = null;
            int toeL = footL >= 0 ? MainChild(si, footL) : -1, toeR = footR >= 0 ? MainChild(si, footR) : -1;
            if (toeL >= 0) fwd += Perp(Pos(si, toeL) - Pos(si, footL), up);
            if (toeR >= 0) fwd += Perp(Pos(si, toeR) - Pos(si, footR), up);
            if (fwd.LengthSquared() > 1e-10f) src = "feet";
            else
            {
                int shL = FindHandNamed(si, -1), shR = FindHandNamed(si, 1);
                if (shL >= 0 && shR >= 0)
                {
                    var left = Perp(Pos(si, shL) - Pos(si, shR), up);
                    if (left.LengthSquared() > 1e-10f) { fwd = Vector3.Cross(Vector3.Normalize(left), up); src = "hands (left × up)"; }
                }
            }
            if (fwd.LengthSquared() > 1e-10f)
            {
                f.Forward = SnapAxis(Vector3.Normalize(fwd), 0.94f);
                f.Up = up;
                f.Left = Vector3.Normalize(Vector3.Cross(f.Up, f.Forward));
                f.Source = src + (haveUp ? ", up from pelvis→head" : "");
            }
            else
            {
                f.Up = up;
                f.Forward = Math.Abs(Vector3.Dot(up, Vector3.UnitZ)) > 0.9f ? -Vector3.UnitY : Vector3.UnitZ;
                f.Forward = Vector3.Normalize(Perp(f.Forward, up));
                f.Left = Vector3.Normalize(Vector3.Cross(f.Up, f.Forward));
                f.Source = "default forward (no feet / hands found)";
            }
            si.FrameCache = f;
            return f;
        }

        private static Vector3 SnapAxis(Vector3 v, float cosThreshold)
        {
            Vector3[] axes = { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ };
            foreach (var a in axes) if (Vector3.Dot(v, a) >= cosThreshold) return a;
            return v;
        }

        /// <summary>v minus its component along unit axis n.</summary>
        public static Vector3 Perp(Vector3 v, Vector3 n) => v - n * Vector3.Dot(v, n);

        /// <summary>The child that continues a chain: prefers children that move skinned vertices, then the deepest
        /// subtree; helpers (IK/twist/socket) are skipped. -1 for a leaf.</summary>
        public static int MainChild(SkelInfo si, int node)
        {
            int best = -1, bestD = -1; bool bestEff = false;
            foreach (int c in si.Children[node])
            {
                if (IsHelper(si, c)) continue;
                int d = si.SubtreeDepth[c]; bool eff = si.Effective[c];
                if (best < 0 || (eff && !bestEff) || (eff == bestEff && d > bestD)) { best = c; bestD = d; bestEff = eff; }
            }
            return best;
        }

        // ------------------------------------------------------------------ hands & fingers

        private static bool IsFingerLike(SkelInfo si, int node) => FingerOfName(si, node) >= 0 || HasToken(si, node, "finger", "digit");

        /// <summary>Hand (wrist) bone of one side: a side-labelled "hand"/"wrist" bone that has finger chains (Mixamo
        /// "LeftHand" rather than "LeftHandIndex1", Unreal "hand_l" rather than the "ik_hand_l" helper); without names,
        /// the side-most bone that fans out into ≥ 3 finger chains. -1 when nothing matches.</summary>
        public static int FindHand(SkelInfo si, int side, List<string> notes)
        {
            if (si == null) return -1;
            int best = FindHandNamed(si, side);
            if (best >= 0) return best;

            // Geometric fallback: a bone that fans out into >= 3 finger chains, on the requested side of the body.
            int n = si.Skel.Nodes.Length;
            var frame = ModelFrame(si);
            int bestG = -1; float bestSide = 0f;
            for (int i = 0; i < n; i++)
            {
                if (!si.Effective[i] || IsHelper(si, i)) continue;
                if (FingerRoots(si, i).Count < 3) continue;
                if (si.Side[i] != 0 && si.Side[i] != side) continue;
                float s = Vector3.Dot(Pos(si, i), frame.Left) * (side < 0 ? 1f : -1f);   // left hand: +Left
                if (bestG < 0 || s > bestSide) { bestG = i; bestSide = s; }
            }
            if (bestG >= 0 && notes != null) notes.Add("hand found by geometry (no side-labelled hand bone)");
            return bestG;
        }

        private static int FindHandNamed(SkelInfo si, int side)
        {
            int n = si.Skel.Nodes.Length;
            int best = -1; double bestScore = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                if (si.Side[i] != side) continue;
                string lw = si.Lower[i];
                if (!Contains(lw, "hand") && !Contains(lw, "wrist")) continue;
                if (IsFingerLike(si, i) || IsHelper(si, i) || !si.Effective[i]) continue;
                int roots = FingerRoots(si, i).Count;
                double score = Math.Min(roots, 5) * 100.0 + (si.Skinned[i] ? 5 : 0) + (IsDefPrefixed(si, i) ? 10 : 0)
                             - (IsOrgPrefixed(si, i) ? 3 : 0) - si.Depth[i] * 0.01;
                if (score > bestScore) { bestScore = score; best = i; }
            }
            return best;
        }

        /// <summary>First bone of every finger chain under a hand. A child that itself branches into several chains
        /// (Rigify palm.01 → thumb + index, DAZ carpals) is a palm/metacarpal node: its children become the roots.</summary>
        public static List<int> FingerRoots(SkelInfo si, int hand)
        {
            var roots = new List<int>();
            if (si != null && hand >= 0) CollectFingerRoots(si, hand, roots, 0);
            return roots;
        }

        private static void CollectFingerRoots(SkelInfo si, int node, List<int> roots, int depth)
        {
            foreach (int c in si.Children[node])
            {
                if (IsHelper(si, c) || !si.Effective[c]) continue;
                int substantial = 0;
                foreach (int g in si.Children[c])
                    if (!IsHelper(si, g) && si.Effective[g] && (si.SubtreeDepth[g] >= 2 || si.Skinned[g])) substantial++;
                if (substantial >= 2 && depth < 3) { CollectFingerRoots(si, c, roots, depth + 1); continue; }
                roots.Add(c);
            }
        }

        /// <summary>The full chain from a finger root down its main path to the leaf (end marker included).</summary>
        public static List<int> ChainFrom(SkelInfo si, int root)
        {
            var chain = new List<int>();
            int cur = root;
            while (cur >= 0 && chain.Count < 16)
            {
                chain.Add(cur);
                cur = MainChild(si, cur);
            }
            return chain;
        }

        /// <summary>
        /// The joints of a raw finger chain a curl should rotate: a leading metacarpal/palm segment is dropped (fingers
        /// only — the thumb's first joint is its CMC and is kept), a trailing end marker is dropped ("Index4", "_end",
        /// "Nub", an unweighted leaf, or the 4th node of a knuckle-started chain), and joints that move no skinned
        /// vertex are dropped. So Mixamo Index1-4 → Index1-3, Unreal index_01-03 → all three, UE5 index_metacarpal +
        /// 01-03 → 01-03, a low-poly thumb whose 3rd joint carries no weights → two joints.
        /// </summary>
        public static int[] CurlJoints(SkelInfo si, List<int> chain, bool isThumb, Vector3 wrist)
        {
            var j = new List<int>(chain);
            if (!isThumb && j.Count >= 2)
            {
                bool byName = HasToken(si, j[0], "metacarpal", "meta", "carpal", "palm");
                float d0 = Vector3.Distance(Pos(si, j[0]), wrist), d1 = Vector3.Distance(Pos(si, j[1]), wrist);
                bool byGeo = j.Count >= 4 && d1 > 1e-6f && d0 < 0.45f * d1;
                if (byName || byGeo) j.RemoveAt(0);
            }
            if (j.Count >= 2)
            {
                int last = j[j.Count - 1];
                bool leaf = si.Children[last].Count == 0;
                if (leaf && (IsEndName(si, last) || !si.Skinned[last] || j.Count >= 4)) j.RemoveAt(j.Count - 1);
            }
            j.RemoveAll(x => !si.Effective[x]);
            return j.ToArray();
        }

        /// <summary>Detected fingers of one hand.</summary>
        public sealed class HandChains
        {
            public int Hand = -1;
            /// <summary>Raw chains (root → leaf, end markers included) per finger slot; null = not found.</summary>
            public List<int>[] Raw = new List<int>[5];
            /// <summary>How each slot was assigned: "names", "geometry", "preset", "explicit".</summary>
            public string[] Source = new string[5];
        }

        /// <summary>
        /// Walk the skeleton under a hand and assign its finger chains to index/middle/ring/pinky/thumb: by name keywords
        /// first, then by GEOMETRY for chains whose names don't say it — the thumb is the chain whose direction deviates
        /// most from the others (and starts nearest the wrist), the remaining chains are sorted across the palm from
        /// the thumb side (index) to the far side (pinky).
        /// </summary>
        public static HandChains DetectFingers(SkelInfo si, int hand, int side, List<string> notes)
        {
            var hc = new HandChains { Hand = hand };
            if (si == null || hand < 0) return hc;
            var roots = FingerRoots(si, hand);
            var chains = new List<List<int>>();
            foreach (int r in roots)
            {
                var ch = ChainFrom(si, r);
                if (ch.Count > 0) chains.Add(ch);
            }
            var unassigned = new List<List<int>>();
            foreach (var ch in chains)
            {
                int f = -1;
                foreach (int node in ch) { f = FingerOfName(si, node); if (f >= 0) break; }
                if (f >= 0 && hc.Raw[f] == null) { hc.Raw[f] = ch; hc.Source[f] = "names"; }
                else if (f >= 0 && hc.Raw[f] != null)
                {
                    // duplicate claim: keep the chain with more joints, the other one competes geometrically
                    if (ch.Count > hc.Raw[f].Count) { unassigned.Add(hc.Raw[f]); hc.Raw[f] = ch; }
                    else unassigned.Add(ch);
                }
                else unassigned.Add(ch);
            }
            if (unassigned.Count == 0) return hc;

            // ---- geometry for the rest
            Vector3 w = Pos(si, hand);
            var all = new List<List<int>>(chains);
            Vector3 meanDir = Vector3.Zero;
            foreach (var ch in all) meanDir += ChainDir(si, ch, w);
            meanDir = meanDir.LengthSquared() > 1e-10f ? Vector3.Normalize(meanDir) : Vector3.UnitX;

            if (hc.Raw[Thumb] == null && all.Count >= 3)
            {
                List<int> thumb = null; double best = double.MinValue;
                float maxD = 1e-6f;
                foreach (var ch in unassigned) maxD = Math.Max(maxD, Vector3.Distance(Pos(si, ch[0]), w));
                foreach (var ch in unassigned)
                {
                    // deviation from the mean direction of the OTHER chains
                    Vector3 others = Vector3.Zero;
                    foreach (var o in all) if (o != ch) others += ChainDir(si, o, w);
                    if (others.LengthSquared() < 1e-10f) continue;
                    others = Vector3.Normalize(others);
                    double ang = Math.Acos(Clamp(Vector3.Dot(ChainDir(si, ch, w), others), -1f, 1f)) * 180.0 / Math.PI;
                    double near = 1.0 - Vector3.Distance(Pos(si, ch[0]), w) / maxD;
                    double score = ang + 25.0 * near;
                    if (ang > 20.0 && score > best) { best = score; thumb = ch; }
                }
                if (thumb != null) { hc.Raw[Thumb] = thumb; hc.Source[Thumb] = "geometry"; unassigned.Remove(thumb); }
            }
            if (unassigned.Count == 0) return hc;

            // Lateral axis across the palm, pointing to the thumb side.
            Vector3 meanBase = Vector3.Zero; int nb = 0;
            foreach (var ch in all) if (ch != hc.Raw[Thumb]) { meanBase += Pos(si, ch[0]); nb++; }
            if (nb > 0) meanBase /= nb;
            Vector3 lateral = Vector3.Zero;
            if (hc.Raw[Thumb] != null) lateral = Perp(Pos(si, hc.Raw[Thumb][0]) - meanBase, meanDir);
            if (lateral.LengthSquared() < 1e-10f && hc.Raw[Index] != null && hc.Raw[Pinky] != null)
                lateral = Perp(Pos(si, hc.Raw[Index][0]) - Pos(si, hc.Raw[Pinky][0]), meanDir);
            if (lateral.LengthSquared() < 1e-10f)
            {
                // T/A-pose thumbs point forward: use the model forward as the thumb side.
                lateral = Perp(ModelFrame(si).Forward, meanDir);
                if (notes != null) notes.Add("finger order across the palm guessed from the character's forward axis");
            }
            lateral = lateral.LengthSquared() > 1e-10f ? Vector3.Normalize(lateral) : Vector3.UnitZ;
            unassigned.Sort((a, b) => Vector3.Dot(Pos(si, b[0]) - meanBase, lateral).CompareTo(Vector3.Dot(Pos(si, a[0]) - meanBase, lateral)));
            int slot = 0;
            foreach (var ch in unassigned)
            {
                while (slot < 4 && hc.Raw[slot] != null) slot++;
                if (slot >= 4) break;
                hc.Raw[slot] = ch; hc.Source[slot] = "geometry";
            }
            return hc;
        }

        private static Vector3 ChainDir(SkelInfo si, List<int> ch, Vector3 wrist)
        {
            Vector3 a = Pos(si, ch[0]), b = Pos(si, ch[ch.Count - 1]);
            Vector3 d = b - a;
            if (d.LengthSquared() < 1e-12f) d = a - wrist;
            return d.LengthSquared() > 1e-12f ? Vector3.Normalize(d) : Vector3.UnitX;
        }

        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);

        /// <summary>Palm geometry of one hand in model space (bind pose).</summary>
        public sealed class HandFrame
        {
            public Vector3 Wrist, Dir, Lateral, Palmar, PalmCenter, ThumbTarget;
            public float PalmLength, HandLength;
            public bool PalmFromThumb;
        }

        /// <summary>
        /// Palm frame: Dir = wrist → knuckles, Lateral = pinky side → thumb side, Palmar = out of the palm (the side a
        /// hand grips with). Palmar comes from the hand's side in the right-handed model space (left hand: Dir × Lateral,
        /// right hand: the opposite; mirrored bind matrices flip it), cross-checked against anatomy — the thumb sits on
        /// the palmar side — so a mislabelled or mirrored hand still closes the right way.
        /// </summary>
        public static HandFrame ComputeHandFrame(SkelInfo si, int hand, List<int>[] raw, int[][] joints, int side, List<string> notes)
        {
            var hf = new HandFrame();
            Vector3 w = Pos(si, hand);
            hf.Wrist = w;
            var knuckles = new List<Vector3>();
            Vector3? kIndex = null, kPinky = null, kMiddle = null;
            for (int f = 0; f < 4; f++)
            {
                if (joints[f] == null || joints[f].Length == 0) continue;
                var p = Pos(si, joints[f][0]);
                knuckles.Add(p);
                if (f == Index) kIndex = p; else if (f == Middle) kMiddle = p; else if (f == Pinky) kPinky = p;
            }
            Vector3 meanK;
            if (knuckles.Count > 0)
            {
                meanK = Vector3.Zero; foreach (var k in knuckles) meanK += k; meanK /= knuckles.Count;
            }
            else
            {
                int c = MainChild(si, hand);
                int par = Parent(si, hand);
                meanK = c >= 0 ? Pos(si, c) : (par >= 0 ? w + (w - Pos(si, par)) * 0.3f : w + Vector3.UnitX * 0.1f);
            }
            Vector3 dir = meanK - w;
            if (dir.LengthSquared() < 1e-12f)
            {
                int par = Parent(si, hand);
                dir = par >= 0 ? w - Pos(si, par) : Vector3.UnitX;
            }
            hf.PalmLength = dir.Length();
            dir = Vector3.Normalize(dir);
            hf.Dir = dir;

            Vector3 lat = Vector3.Zero;
            if (kIndex.HasValue && kPinky.HasValue) lat = Perp(kIndex.Value - kPinky.Value, dir);
            bool haveThumb = raw[Thumb] != null && raw[Thumb].Count > 0;
            if (lat.LengthSquared() < 1e-12f && haveThumb) lat = Perp(Pos(si, raw[Thumb][0]) - meanK, dir);
            if (lat.LengthSquared() < 1e-12f) lat = Perp(ModelFrame(si).Forward, dir);
            if (lat.LengthSquared() < 1e-12f) lat = Perp(Vector3.UnitZ, dir);
            lat = Vector3.Normalize(lat);
            hf.Lateral = lat;

            float sideSign = side < 0 ? 1f : -1f;
            if (Mirrored(si.Bind[hand])) sideSign = -sideSign;
            Vector3 palmar = Vector3.Normalize(Vector3.Cross(dir, lat) * sideSign);

            // Anatomical cross-check: the thumb (CMC → tip) lies on the palmar side of the palm plane.
            if (haveThumb)
            {
                Vector3 tm = Vector3.Zero;
                foreach (int t in raw[Thumb]) tm += Pos(si, t);
                tm /= raw[Thumb].Count;
                float s = Vector3.Dot(tm - w, palmar);
                if (s < -0.12f * Math.Max(hf.PalmLength, 1e-6f))
                {
                    palmar = -palmar;
                    hf.PalmFromThumb = true;
                    if (notes != null) notes.Add("palm side taken from the thumb position (the side label and the hand's geometry disagree)");
                }
            }
            hf.Palmar = palmar;

            // Proportions from the palm only (wrist → knuckles), so rigs with and without end bones agree.
            hf.HandLength = hf.PalmLength * 1.65f;
            hf.PalmCenter = w + (meanK - w) * 0.55f;
            Vector3 grip = kIndex.HasValue && kMiddle.HasValue ? (kIndex.Value + kMiddle.Value) * 0.5f : meanK;
            // Where a closing thumb heads: over the index/middle finger roots, out on the palmar side (where the
            // curled index/middle middle phalanges lie in a fist).
            hf.ThumbTarget = grip + palmar * (0.3f * hf.PalmLength) - dir * (0.1f * hf.PalmLength);
            return hf;
        }

        /// <summary>True when a matrix mirrors (negative determinant of its 3×3).</summary>
        public static bool Mirrored(Matrix4x4 m)
        {
            float det = m.M11 * (m.M22 * m.M33 - m.M23 * m.M32)
                      - m.M12 * (m.M21 * m.M33 - m.M23 * m.M31)
                      + m.M13 * (m.M21 * m.M32 - m.M22 * m.M31);
            return det < 0f;
        }

        // ------------------------------------------------------------------ head / neck / spine

        /// <summary>Head bone: a "head" bone that is not an end marker/eye/jaw helper (Mixamo "Head" rather than
        /// "HeadTop_End"); without one, the top of the neck line above the chest (Rigify "spine.006").</summary>
        public static int FindHead(SkelInfo si, List<string> notes)
        {
            if (si == null) return -1;
            int best = FindHeadNamed(si);
            if (best >= 0) return best;
            int chest = FindChest(si);
            if (chest < 0) return -1;
            int line = NeckLineRoot(si, chest);
            if (line < 0) return -1;
            int cur = line, last = line;
            while (cur >= 0) { if (si.Effective[cur] && !IsEndName(si, cur)) last = cur; cur = MainChild(si, cur); }
            if (notes != null) notes.Add("head found as the top of the neck line (no bone named 'head')");
            return last;
        }

        private static int FindHeadNamed(SkelInfo si)
        {
            int n = si.Skel.Nodes.Length, best = -1; double bestScore = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                if (!HasToken(si, i, "head") || si.Side[i] != 0) continue;
                if (IsEndName(si, i) || IsHelper(si, i) || HasToken(si, i, "eye", "jaw", "hair", "tongue", "teeth")) continue;
                if (!si.Effective[i]) continue;
                double score = (si.Skinned[i] ? 10 : 0) + (IsDefPrefixed(si, i) ? 5 : 0) - (IsOrgPrefixed(si, i) ? 3 : 0) - si.Depth[i] * 0.01;
                if (score > bestScore) { bestScore = score; best = i; }
            }
            return best;
        }

        /// <summary>The chest: lowest common ancestor of both arms (the bone both shoulders hang from).</summary>
        public static int FindChest(SkelInfo si)
        {
            int hl = FindHand(si, -1, null), hr = FindHand(si, 1, null);
            if (hl < 0 || hr < 0) return -1;
            return Lca(si, hl, hr);
        }

        private static int NeckLineRoot(SkelInfo si, int chest)
        {
            int hl = FindHand(si, -1, null), hr = FindHand(si, 1, null);
            int best = -1, bestD = -1;
            foreach (int c in si.Children[chest])
            {
                if (IsHelper(si, c) || !si.Effective[c]) continue;
                if ((hl >= 0 && (c == hl || IsAncestor(si, c, hl))) || (hr >= 0 && (c == hr || IsAncestor(si, c, hr)))) continue;
                if (si.Side[c] != 0) continue;
                if (si.SubtreeDepth[c] > bestD) { best = c; bestD = si.SubtreeDepth[c]; }
            }
            return best;
        }

        /// <summary>Neck bones (chest → head order, exclusive of both) and up to two spine bones below the neck
        /// (chest first, then its parent) for a look-at chain.</summary>
        public static void FindNeckAndSpine(SkelInfo si, int head, List<int> neck, List<int> spine)
        {
            if (si == null || head < 0) return;
            int chest = FindChest(si);
            var ups = new List<int>();
            if (chest >= 0 && IsAncestor(si, chest, head))
            {
                for (int p = Parent(si, head); p >= 0 && p != chest; p = Parent(si, p))
                    if (si.Effective[p] && !IsHelper(si, p)) ups.Add(p);
            }
            else
            {
                for (int p = Parent(si, head), k = 0; p >= 0 && k < 2; p = Parent(si, p))
                    if (HasToken(si, p, "neck") && !IsHelper(si, p)) { ups.Add(p); k++; } else break;
            }
            ups.Reverse();
            neck.AddRange(ups);
            int s0 = chest >= 0 && IsAncestor(si, chest, head) ? chest : (ups.Count > 0 ? Parent(si, ups[0]) : Parent(si, head));
            if (s0 >= 0 && si.Effective[s0]) spine.Add(s0);
            int s1 = s0 >= 0 ? Parent(si, s0) : -1;
            if (s1 >= 0 && si.Effective[s1] && (HasToken(si, s1, "spine", "chest", "torso", "back") || Contains(si.Lower[s1], "spine") || Contains(si.Lower[s1], "chest")))
                spine.Add(s1);
        }

        // ------------------------------------------------------------------ legs

        /// <summary>Foot (ankle) bone of one side: a side-labelled "foot"/"ankle" bone that is not a toe/ball/IK helper
        /// and hangs below a two-bone leg. -1 when nothing matches.</summary>
        public static int FindFoot(SkelInfo si, int side)
        {
            if (si == null) return -1;
            int n = si.Skel.Nodes.Length, best = -1; double bestScore = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                if (si.Side[i] != side) continue;
                string lw = si.Lower[i];
                if (!Contains(lw, "foot") && !Contains(lw, "ankle")) continue;
                if (HasToken(si, i, "toe", "toes", "ball", "heel") || Contains(lw, "toe") || IsEndName(si, i) || IsHelper(si, i)) continue;
                if (!si.Effective[i]) continue;
                int mid, root;
                if (!ResolveLimb(si, i, out mid, out root)) continue;
                double score = (si.Skinned[i] ? 10 : 0) + (IsDefPrefixed(si, i) ? 5 : 0) - (IsOrgPrefixed(si, i) ? 3 : 0) - si.Depth[i] * 0.01;
                if (score > bestScore) { bestScore = score; best = i; }
            }
            return best;
        }

        /// <summary>Height of the ankle above the sole along <paramref name="up"/> (bind pose): ankle minus the lowest
        /// joint of the foot's subtree (toes / toe end).</summary>
        public static float FootHeight(SkelInfo si, int foot, Vector3 up)
        {
            float ankle = Vector3.Dot(Pos(si, foot), up), lowest = ankle;
            var stack = new Stack<int>();
            stack.Push(foot);
            while (stack.Count > 0)
            {
                int x = stack.Pop();
                lowest = Math.Min(lowest, Vector3.Dot(Pos(si, x), up));
                foreach (int c in si.Children[x]) stack.Push(c);
            }
            return Math.Max(0f, ankle - lowest);
        }
    }
}
