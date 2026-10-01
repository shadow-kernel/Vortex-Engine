using System.Runtime.Serialization;

namespace Editor.ECS.Components.Animation
{
    /// <summary>Which hand a <see cref="HandPose"/> shapes.</summary>
    public enum HandSide { Left = 0, Right = 1 }

    /// <summary>
    /// Skeleton naming convention the rig-aware components (<see cref="HandPose"/>, <see cref="LookAtIk"/>,
    /// <see cref="FootIk"/>) resolve their bones with. <see cref="Auto"/> works on any rig — bone names in every
    /// common convention, bind-pose geometry as the fallback; a preset only makes the lookup deterministic (and falls
    /// back to auto-detection when its names are not in the skeleton); <see cref="Custom"/> uses only the bones listed
    /// explicitly on the component.
    /// </summary>
    public enum RigPreset { Auto = 0, Mixamo = 1, Unreal = 2, UnityGeneric = 3, Rigify = 4, Custom = 5 }

    /// <summary>Finger pose presets for <see cref="HandPose.ApplyPreset"/> and <c>Animation.SetHandPose</c>.</summary>
    public enum HandPosePreset { Open = 0, Relaxed = 1, Fist = 2, Trigger = 3, Grip = 4, Point = 5 }

    /// <summary>
    /// Editor-authored finger pose (the "grip" of a hand): a per-finger curl in degrees for the joints of index, middle,
    /// ring, pinky and thumb, applied every frame as an ADDITIVE rotation on top of whatever the animation does — so a
    /// weapon hold clip whose fingers hover next to the grip closes around it without any script. Sits on the entity
    /// that carries the Animator; one component per hand.
    ///
    /// Works on ANY skeleton: <see cref="Rig"/> = Auto (the default for new components) finds the hand bone of
    /// <see cref="Side"/> and walks the finger chains under it (names in any convention — Mixamo, Unreal, Rigify,
    /// Unity/VRM, Biped, DAZ, CC — or, when names don't say it, the chains' geometry around the palm), drops end markers
    /// and metacarpals, and maps the three curl values onto however many joints a finger has (1..4). The curl axis of
    /// every joint is computed from the bind pose (perpendicular to the finger and the palm normal, signed so that
    /// positive degrees CLOSE the hand on both sides), so no rig-specific axis/sign has to be known. <see cref="HandBone"/>
    /// and the per-finger bone lists override detection; <see cref="CurlAxis"/> 0..2 forces a manual local axis.
    ///
    /// Backward compatible: components saved before the rig options existed load as <see cref="RigPreset.Mixamo"/> with
    /// their manual axis — exactly the old behaviour (names "{prefix}{Left|Right}Hand{Finger}{1..3}", curl around the
    /// local <see cref="CurlAxis"/> with <see cref="CurlSign"/>).
    /// </summary>
    [DataContract(Name = "HandPose", Namespace = "")]
    public class HandPose : Component
    {
        private const string DefaultFormat = "{0}{1}Hand{2}{3}";

        private HandSide _side = HandSide.Right;
        private string _bonePrefix = "mixamorig:";
        private string _boneFormat = DefaultFormat;
        private int _curlAxis = -1;              // new components: automatic axis from the bind pose
        private float _curlSign = -1f;
        private float _weight = 1f;
        private Vector3 _index = new Vector3(55f, 80f, 45f);
        private Vector3 _middle = new Vector3(60f, 85f, 50f);
        private Vector3 _ring = new Vector3(60f, 85f, 50f);
        private Vector3 _pinky = new Vector3(60f, 85f, 50f);
        private Vector3 _thumb = new Vector3(20f, 30f, 15f);
        private float _spread = 0f;
        private RigPreset _rig = RigPreset.Auto;  // new components: auto-detect
        private string _handBone = "";
        private string _indexBones = "";
        private string _middleBones = "";
        private string _ringBones = "";
        private string _pinkyBones = "";
        private string _thumbBones = "";

        public override string DisplayName => "Hand Pose";
        public override string IconCode => "";   // MDL2 glyph
        public override string IconColor => "#C586C0";

        /// <summary>Left or right hand — selects the hand bone (and the palm side).</summary>
        [DataMember(Name = "side", Order = 10)]
        public HandSide Side { get => _side; set { if (SetProperty(ref _side, value, nameof(Side))) Changed(); } }

        /// <summary>Mixamo preset: bone name prefix ("mixamorig:"; a different prefix in the skeleton is detected).</summary>
        [DataMember(Name = "bonePrefix", Order = 11)]
        public string BonePrefix { get => _bonePrefix; set { if (SetProperty(ref _bonePrefix, value ?? "", nameof(BonePrefix))) Changed(); } }

        /// <summary>Mixamo preset naming pattern: {0} prefix, {1} Left/Right, {2} finger (Index/Middle/Ring/Pinky/Thumb), {3} joint 1..3.</summary>
        [DataMember(Name = "boneFormat", Order = 12)]
        public string BoneFormat { get => _boneFormat; set { if (SetProperty(ref _boneFormat, string.IsNullOrEmpty(value) ? DefaultFormat : value, nameof(BoneFormat))) Changed(); } }

        /// <summary>Curl axis: -1 = automatic per joint from the bind pose (recommended, rig-independent);
        /// 0/1/2 = force the joint's local X/Y/Z (manual override, turned by <see cref="CurlSign"/>).</summary>
        [DataMember(Name = "curlAxis", Order = 13)]
        public int CurlAxis { get => _curlAxis; set { if (SetProperty(ref _curlAxis, value < -1 ? -1 : (value > 2 ? 2 : value), nameof(CurlAxis))) Changed(); } }

        /// <summary>Manual axis only: +1 or -1, which way around the local axis closes the hand (Mixamo X: -1).
        /// The automatic axis is always signed so that positive degrees close the hand.</summary>
        [DataMember(Name = "curlSign", Order = 14)]
        public float CurlSign { get => _curlSign; set { if (SetProperty(ref _curlSign, value < 0f ? -1f : 1f, nameof(CurlSign))) Changed(); } }

        /// <summary>0 = animation only, 1 = full pose.</summary>
        [DataMember(Name = "weight", Order = 15)]
        public float Weight { get => _weight; set { if (SetProperty(ref _weight, value < 0f ? 0f : (value > 1f ? 1f : value), nameof(Weight))) Changed(); } }

        /// <summary>Index finger curl in degrees for joint 1 / 2 / 3 (knuckle → tip). Positive closes the hand.</summary>
        [DataMember(Name = "index", Order = 20)]
        public Vector3 Index { get => _index; set { if (SetProperty(ref _index, value, nameof(Index))) Changed(); } }
        [DataMember(Name = "middle", Order = 21)]
        public Vector3 Middle { get => _middle; set { if (SetProperty(ref _middle, value, nameof(Middle))) Changed(); } }
        [DataMember(Name = "ring", Order = 22)]
        public Vector3 Ring { get => _ring; set { if (SetProperty(ref _ring, value, nameof(Ring))) Changed(); } }
        [DataMember(Name = "pinky", Order = 23)]
        public Vector3 Pinky { get => _pinky; set { if (SetProperty(ref _pinky, value, nameof(Pinky))) Changed(); } }
        /// <summary>Thumb curl (degrees per joint: CMC / MCP / IP). With the automatic axis the thumb closes across
        /// the palm (toward the index/middle roots); with a manual axis it bends around that axis.</summary>
        [DataMember(Name = "thumb", Order = 24)]
        public Vector3 Thumb { get => _thumb; set { if (SetProperty(ref _thumb, value, nameof(Thumb))) Changed(); } }

        /// <summary>Sideways splay of the fingers at the knuckle (degrees; positive spreads them apart).
        /// 0 keeps the animation's spacing.</summary>
        [DataMember(Name = "spread", Order = 25)]
        public float Spread { get => _spread; set { if (SetProperty(ref _spread, value, nameof(Spread))) Changed(); } }

        /// <summary>Naming convention used to find the bones (Auto = detect on any rig).</summary>
        [DataMember(Name = "rig", Order = 30)]
        public RigPreset Rig { get => _rig; set { if (SetProperty(ref _rig, value, nameof(Rig))) Changed(); } }

        /// <summary>Wrist bone of this hand (empty = detect from <see cref="Side"/>). Everything else can be derived from it.</summary>
        [DataMember(Name = "handBone", Order = 31)]
        public string HandBone { get => _handBone; set { if (SetProperty(ref _handBone, value ?? "", nameof(HandBone))) Changed(); } }

        /// <summary>Explicit index finger joints, knuckle → tip, comma separated ("a,b,c"; 1..4 joints).
        /// Used by the Custom rig; with any other rig a non-empty list overrides the detected chain of that finger.</summary>
        [DataMember(Name = "indexBones", Order = 32)]
        public string IndexBones { get => _indexBones; set { if (SetProperty(ref _indexBones, value ?? "", nameof(IndexBones))) Changed(); } }
        [DataMember(Name = "middleBones", Order = 33)]
        public string MiddleBones { get => _middleBones; set { if (SetProperty(ref _middleBones, value ?? "", nameof(MiddleBones))) Changed(); } }
        [DataMember(Name = "ringBones", Order = 34)]
        public string RingBones { get => _ringBones; set { if (SetProperty(ref _ringBones, value ?? "", nameof(RingBones))) Changed(); } }
        [DataMember(Name = "pinkyBones", Order = 35)]
        public string PinkyBones { get => _pinkyBones; set { if (SetProperty(ref _pinkyBones, value ?? "", nameof(PinkyBones))) Changed(); } }
        [DataMember(Name = "thumbBones", Order = 36)]
        public string ThumbBones { get => _thumbBones; set { if (SetProperty(ref _thumbBones, value ?? "", nameof(ThumbBones))) Changed(); } }

        private void Changed()
        {
            if (Entity != null) Core.Animation.AnimationService.Instance.RefreshIk(Entity);   // re-pose now (edit-mode preview + play)
        }

        public HandPose() : base() { }
        public HandPose(GameEntity entity) : base(entity) { }

        /// <summary>DataContractSerializer creates this object UNINITIALIZED — restore non-trivial defaults. Members that
        /// did not exist in older files keep the LEGACY meaning: no "rig" member = the Mixamo naming pattern, and the
        /// stored curlAxis (0..2) stays a manual axis — old scenes/prefabs pose exactly as before.</summary>
        [OnDeserializing]
        private void OnDeserializingMethod(StreamingContext context)
        {
            _side = HandSide.Right; _bonePrefix = "mixamorig:"; _boneFormat = DefaultFormat;
            _curlAxis = 0; _curlSign = -1f; _weight = 1f;
            _index = new Vector3(55f, 80f, 45f); _middle = new Vector3(60f, 85f, 50f); _ring = new Vector3(60f, 85f, 50f);
            _pinky = new Vector3(60f, 85f, 50f); _thumb = new Vector3(20f, 30f, 15f); _spread = 0f;
            _rig = RigPreset.Mixamo; _handBone = "";
            _indexBones = ""; _middleBones = ""; _ringBones = ""; _pinkyBones = ""; _thumbBones = "";
        }

        /// <summary>Mixamo-pattern bone name of one finger joint (finger = "Index".."Thumb", joint 1..3).</summary>
        public string BoneName(string finger, int joint)
            => BoneName(_bonePrefix, finger, joint);

        /// <summary>Same with an explicit prefix (the service passes a prefix detected in the skeleton).</summary>
        public string BoneName(string prefix, string finger, int joint)
        {
            try { return string.Format(_boneFormat, prefix, _side == HandSide.Left ? "Left" : "Right", finger, joint); }
            catch { return prefix + (_side == HandSide.Left ? "Left" : "Right") + "Hand" + finger + joint; }
        }

        public static readonly string[] Fingers = { "Index", "Middle", "Ring", "Pinky", "Thumb" };

        /// <summary>Curl for a finger (degrees per joint): 0 index, 1 middle, 2 ring, 3 pinky, 4 thumb.</summary>
        public Vector3 CurlOf(int finger)
        {
            switch (finger) { case 0: return _index; case 1: return _middle; case 2: return _ring; case 3: return _pinky; default: return _thumb; }
        }

        /// <summary>Explicit joint list of a finger (0 index .. 4 thumb), "" when not set.</summary>
        public string ExplicitBones(int finger)
        {
            switch (finger) { case 0: return _indexBones; case 1: return _middleBones; case 2: return _ringBones; case 3: return _pinkyBones; default: return _thumbBones; }
        }

        /// <summary>True for the untouched legacy configuration (Mixamo pattern + manual axis) — posed exactly like
        /// components authored before the rig options existed.</summary>
        public bool IsLegacyConfiguration => _rig == RigPreset.Mixamo && _curlAxis >= 0;

        // ------------------------------------------------------------------ presets

        /// <summary>Curl values of a pose preset (degrees per joint, positive closes; spread in degrees).</summary>
        public static void GetPreset(HandPosePreset preset, out Vector3 index, out Vector3 middle, out Vector3 ring,
                                     out Vector3 pinky, out Vector3 thumb, out float spread)
        {
            switch (preset)
            {
                case HandPosePreset.Open:
                    index = middle = ring = pinky = new Vector3(0f, 0f, 0f);
                    thumb = new Vector3(0f, 0f, 0f); spread = 4f; break;
                case HandPosePreset.Relaxed:
                    index = new Vector3(12f, 18f, 10f); middle = new Vector3(16f, 22f, 12f);
                    ring = new Vector3(20f, 26f, 14f); pinky = new Vector3(24f, 30f, 16f);
                    thumb = new Vector3(8f, 10f, 6f); spread = 0f; break;
                case HandPosePreset.Fist:
                    index = middle = ring = pinky = new Vector3(88f, 100f, 65f);
                    thumb = new Vector3(40f, 40f, 30f); spread = 0f; break;
                case HandPosePreset.Trigger:
                    index = new Vector3(25f, 40f, 20f);
                    middle = ring = pinky = new Vector3(75f, 90f, 50f);
                    thumb = new Vector3(30f, 30f, 20f); spread = 0f; break;
                case HandPosePreset.Grip:
                    index = middle = ring = pinky = new Vector3(60f, 75f, 40f);
                    thumb = new Vector3(30f, 25f, 15f); spread = 0f; break;
                case HandPosePreset.Point:
                    index = new Vector3(0f, 0f, 0f);
                    middle = ring = pinky = new Vector3(88f, 100f, 65f);
                    thumb = new Vector3(40f, 40f, 30f); spread = 0f; break;
                default:
                    index = middle = ring = pinky = thumb = new Vector3(0f, 0f, 0f); spread = 0f; break;
            }
        }

        /// <summary>Set the five finger curls + spread from a preset (one re-pose).</summary>
        public void ApplyPreset(HandPosePreset preset)
        {
            Vector3 i, m, r, p, t; float s;
            GetPreset(preset, out i, out m, out r, out p, out t, out s);
            SetCurls(i, m, r, p, t, s);
        }

        /// <summary>Set all curls at once (one re-pose instead of six).</summary>
        public void SetCurls(Vector3 index, Vector3 middle, Vector3 ring, Vector3 pinky, Vector3 thumb, float spread)
        {
            bool any = false;
            any |= SetProperty(ref _index, index, nameof(Index));
            any |= SetProperty(ref _middle, middle, nameof(Middle));
            any |= SetProperty(ref _ring, ring, nameof(Ring));
            any |= SetProperty(ref _pinky, pinky, nameof(Pinky));
            any |= SetProperty(ref _thumb, thumb, nameof(Thumb));
            any |= SetProperty(ref _spread, spread, nameof(Spread));
            if (any) Changed();
        }

        // ------------------------------------------------------------------ mirroring

        /// <summary>Copy this hand's pose and rig settings to <paramref name="other"/> as the OPPOSITE hand: curls, spread,
        /// weight, rig preset, prefix/pattern and curl axis; the explicit hand bone and finger lists are mirrored by
        /// swapping their side tokens ("LeftHand" ↔ "RightHand", "hand_l" ↔ "hand_r", ".L" ↔ ".R"). A manual axis sign
        /// is copied as-is (mirrored skeletons keep the same local convention per side in Mixamo/Unreal rigs); the
        /// automatic axis handles both sides by construction.</summary>
        public void CopyMirroredTo(HandPose other)
        {
            if (other == null || other == this) return;
            other._side = _side == HandSide.Left ? HandSide.Right : HandSide.Left;
            other._rig = _rig;
            other._bonePrefix = _bonePrefix;
            other._boneFormat = _boneFormat;
            other._curlAxis = _curlAxis;
            other._curlSign = _curlSign;
            other._weight = _weight;
            other._handBone = Core.Animation.RigMap.MirrorName(_handBone);
            other._indexBones = MirrorList(_indexBones);
            other._middleBones = MirrorList(_middleBones);
            other._ringBones = MirrorList(_ringBones);
            other._pinkyBones = MirrorList(_pinkyBones);
            other._thumbBones = MirrorList(_thumbBones);
            other.SetCurls(_index, _middle, _ring, _pinky, _thumb, _spread);
            foreach (var n in new[] { nameof(Side), nameof(Rig), nameof(BonePrefix), nameof(BoneFormat), nameof(CurlAxis), nameof(CurlSign),
                                      nameof(Weight), nameof(HandBone), nameof(IndexBones), nameof(MiddleBones), nameof(RingBones),
                                      nameof(PinkyBones), nameof(ThumbBones) })
                other.OnPropertyChanged(n);
            other.Changed();
        }

        /// <summary>Mirror this pose onto the entity's other hand: reuses that hand's HandPose component or adds one.
        /// Returns the other hand's component (null without an entity).</summary>
        public HandPose MirrorToOtherHand()
        {
            if (Entity == null) return null;
            var otherSide = _side == HandSide.Left ? HandSide.Right : HandSide.Left;
            HandPose other = null;
            foreach (var c in Entity.Components)
            {
                var h = c as HandPose;
                if (h != null && h != this && h.Side == otherSide) { other = h; break; }
            }
            if (other == null)
            {
                other = new HandPose(Entity);
                Entity.AddComponent(other);
            }
            CopyMirroredTo(other);
            return other;
        }

        private static string MirrorList(string list)
        {
            if (string.IsNullOrWhiteSpace(list)) return list ?? "";
            var parts = list.Split(',');
            for (int i = 0; i < parts.Length; i++) parts[i] = Core.Animation.RigMap.MirrorName(parts[i].Trim());
            return string.Join(",", parts);
        }
    }
}
