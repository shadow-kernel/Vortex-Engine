using System.Runtime.Serialization;

namespace Editor.ECS.Components.Animation
{
    /// <summary>Which hand a <see cref="HandPose"/> shapes.</summary>
    public enum HandSide { Left = 0, Right = 1 }

    /// <summary>
    /// Editor-authored finger pose (the "grip" of a hand): a per-finger curl in degrees for the three joints of
    /// index, middle, ring, pinky and thumb, applied every frame as an ADDITIVE rotation on top of whatever the
    /// animation does — so a weapon hold clip whose fingers hover next to the grip closes around it without any
    /// script. Sits on the entity that carries the Animator (the character / FP arms rig); several HandPose
    /// components per rig are fine (one per hand). Bone names are built from <see cref="BonePrefix"/> +
    /// side + "Hand" + finger + joint index (Mixamo: "mixamorig:LeftHandIndex1"); other rigs set the prefix
    /// or override the naming pattern via <see cref="BoneFormat"/> ({0} = prefix, {1} = Left/Right,
    /// {2} = finger name, {3} = joint 1..3).
    /// The curl rotates around the joint's local <see cref="CurlAxis"/> (Mixamo: X, sign -1 closes the hand);
    /// <see cref="Weight"/> blends the whole pose. Live-previewed in the editor viewport (bind pose + curl).
    /// </summary>
    [DataContract(Name = "HandPose", Namespace = "")]
    public class HandPose : Component
    {
        private HandSide _side = HandSide.Right;
        private string _bonePrefix = "mixamorig:";
        private string _boneFormat = "{0}{1}Hand{2}{3}";
        private int _curlAxis = 0;
        private float _curlSign = -1f;
        private float _weight = 1f;
        private Vector3 _index = new Vector3(55f, 80f, 45f);
        private Vector3 _middle = new Vector3(60f, 85f, 50f);
        private Vector3 _ring = new Vector3(60f, 85f, 50f);
        private Vector3 _pinky = new Vector3(60f, 85f, 50f);
        private Vector3 _thumb = new Vector3(20f, 30f, 15f);
        private float _spread = 0f;

        public override string DisplayName => "Hand Pose";
        public override string IconCode => "";   // MDL2 glyph
        public override string IconColor => "#C586C0";

        /// <summary>Left or right hand — selects the bone names.</summary>
        [DataMember(Name = "side", Order = 10)]
        public HandSide Side { get => _side; set { if (SetProperty(ref _side, value, nameof(Side))) Changed(); } }

        /// <summary>Bone name prefix of the rig ("mixamorig:" for Mixamo characters, "" for most others).</summary>
        [DataMember(Name = "bonePrefix", Order = 11)]
        public string BonePrefix { get => _bonePrefix; set { if (SetProperty(ref _bonePrefix, value ?? "", nameof(BonePrefix))) Changed(); } }

        /// <summary>Naming pattern: {0} prefix, {1} Left/Right, {2} finger (Index/Middle/Ring/Pinky/Thumb), {3} joint 1..3.</summary>
        [DataMember(Name = "boneFormat", Order = 12)]
        public string BoneFormat { get => _boneFormat; set { if (SetProperty(ref _boneFormat, string.IsNullOrEmpty(value) ? "{0}{1}Hand{2}{3}" : value, nameof(BoneFormat))) Changed(); } }

        /// <summary>Local axis the finger joints bend around: 0 = X, 1 = Y, 2 = Z (Mixamo: X).</summary>
        [DataMember(Name = "curlAxis", Order = 13)]
        public int CurlAxis { get => _curlAxis; set { if (SetProperty(ref _curlAxis, value < 0 ? 0 : (value > 2 ? 2 : value), nameof(CurlAxis))) Changed(); } }

        /// <summary>+1 or -1: which way around the axis closes the hand (Mixamo: -1).</summary>
        [DataMember(Name = "curlSign", Order = 14)]
        public float CurlSign { get => _curlSign; set { if (SetProperty(ref _curlSign, value < 0f ? -1f : 1f, nameof(CurlSign))) Changed(); } }

        /// <summary>0 = animation only, 1 = full pose.</summary>
        [DataMember(Name = "weight", Order = 15)]
        public float Weight { get => _weight; set { if (SetProperty(ref _weight, value < 0f ? 0f : (value > 1f ? 1f : value), nameof(Weight))) Changed(); } }

        /// <summary>Index finger curl in degrees for joint 1 / 2 / 3 (knuckle → tip).</summary>
        [DataMember(Name = "index", Order = 20)]
        public Vector3 Index { get => _index; set { if (SetProperty(ref _index, value, nameof(Index))) Changed(); } }
        [DataMember(Name = "middle", Order = 21)]
        public Vector3 Middle { get => _middle; set { if (SetProperty(ref _middle, value, nameof(Middle))) Changed(); } }
        [DataMember(Name = "ring", Order = 22)]
        public Vector3 Ring { get => _ring; set { if (SetProperty(ref _ring, value, nameof(Ring))) Changed(); } }
        [DataMember(Name = "pinky", Order = 23)]
        public Vector3 Pinky { get => _pinky; set { if (SetProperty(ref _pinky, value, nameof(Pinky))) Changed(); } }
        /// <summary>Thumb curl (degrees per joint) — the thumb bends around the same local axis on Mixamo rigs.</summary>
        [DataMember(Name = "thumb", Order = 24)]
        public Vector3 Thumb { get => _thumb; set { if (SetProperty(ref _thumb, value, nameof(Thumb))) Changed(); } }

        /// <summary>Sideways splay of the fingers at the knuckle (degrees, around the local axis after the curl axis;
        /// positive spreads them apart). 0 keeps the animation's spacing.</summary>
        [DataMember(Name = "spread", Order = 25)]
        public float Spread { get => _spread; set { if (SetProperty(ref _spread, value, nameof(Spread))) Changed(); } }

        private void Changed()
        {
            if (Entity != null) Core.Animation.AnimationService.Instance.RefreshIk(Entity);   // re-pose now (edit-mode preview + play)
        }

        public HandPose() : base() { }
        public HandPose(GameEntity entity) : base(entity) { }

        /// <summary>DataContractSerializer creates this object UNINITIALIZED — restore non-trivial defaults.</summary>
        [OnDeserializing]
        private void OnDeserializingMethod(StreamingContext context)
        {
            _side = HandSide.Right; _bonePrefix = "mixamorig:"; _boneFormat = "{0}{1}Hand{2}{3}";
            _curlAxis = 0; _curlSign = -1f; _weight = 1f;
            _index = new Vector3(55f, 80f, 45f); _middle = new Vector3(60f, 85f, 50f); _ring = new Vector3(60f, 85f, 50f);
            _pinky = new Vector3(60f, 85f, 50f); _thumb = new Vector3(20f, 30f, 15f); _spread = 0f;
        }

        /// <summary>Bone name of one finger joint (finger = "Index".."Thumb", joint 1..3).</summary>
        public string BoneName(string finger, int joint)
        {
            try { return string.Format(_boneFormat, _bonePrefix, _side == HandSide.Left ? "Left" : "Right", finger, joint); }
            catch { return _bonePrefix + (_side == HandSide.Left ? "Left" : "Right") + "Hand" + finger + joint; }
        }

        public static readonly string[] Fingers = { "Index", "Middle", "Ring", "Pinky", "Thumb" };

        /// <summary>Curl for a finger (degrees per joint).</summary>
        public Vector3 CurlOf(int finger)
        {
            switch (finger) { case 0: return _index; case 1: return _middle; case 2: return _ring; case 3: return _pinky; default: return _thumb; }
        }
    }
}
