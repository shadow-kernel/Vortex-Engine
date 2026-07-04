using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Editor.ECS.Components.Animation
{
    /// <summary>One named clip slot in an Animator ("Walk" -> "Assets/Animations/Walk.vanim").</summary>
    [DataContract(Name = "AnimatorClip", Namespace = "")]
    public class AnimatorClipEntry
    {
        private string _name = "";
        private string _path = "";

        [DataMember(Name = "name", Order = 0)]
        public string Name { get => _name; set => _name = value ?? ""; }

        /// <summary>Project-relative .vanim path.</summary>
        [DataMember(Name = "path", Order = 1)]
        public string Path { get => _path; set => _path = value ?? ""; }
    }

    /// <summary>
    /// Animator component: a named clip table + playback defaults. State machines are game logic and
    /// live in scripts (PlayAnimation/CrossFade) - this component only carries the data.
    /// Playback/pose evaluation happens in Core.Animation.AnimationService.
    /// </summary>
    [DataContract(Name = "Animator", Namespace = "")]
    public class Animator : Component
    {
        private List<AnimatorClipEntry> _clips = new List<AnimatorClipEntry>();
        private string _defaultClip = "";
        private bool _playOnStart = true;
        private float _speed = 1f;

        public override string DisplayName => "Animator";
        public override string IconCode => "";   // MDL2 play glyph
        public override string IconColor => "#C586C0";

        /// <summary>Named clips scripts can play by name.</summary>
        [DataMember(Name = "clips", Order = 10)]
        public List<AnimatorClipEntry> Clips
        {
            get => _clips ?? (_clips = new List<AnimatorClipEntry>());
            set => SetProperty(ref _clips, value ?? new List<AnimatorClipEntry>(), nameof(Clips));
        }

        /// <summary>Clip name (from the table) or .vanim path played on start when PlayOnStart is set.</summary>
        [DataMember(Name = "defaultClip", Order = 11)]
        public string DefaultClip
        {
            get => _defaultClip;
            set => SetProperty(ref _defaultClip, value ?? "", nameof(DefaultClip));
        }

        [DataMember(Name = "playOnStart", Order = 12)]
        public bool PlayOnStart
        {
            get => _playOnStart;
            set => SetProperty(ref _playOnStart, value, nameof(PlayOnStart));
        }

        /// <summary>Global playback speed multiplier for this Animator.</summary>
        [DataMember(Name = "speed", Order = 13)]
        public float Speed
        {
            get => _speed;
            set => SetProperty(ref _speed, value, nameof(Speed));
        }

        public Animator() : base() { }
        public Animator(GameEntity entity) : base(entity) { }

        /// <summary>DataContractSerializer creates this object UNINITIALIZED (no ctor, no field initializers),
        /// so restore the non-trivial defaults here. Without this, a .ventity/scene missing "speed" deserializes
        /// to Speed=0 — which silently freezes playback at frame 0 (dt is multiplied by Speed).</summary>
        [OnDeserializing]
        private void OnDeserializingMethod(StreamingContext context)
        {
            _clips = new List<AnimatorClipEntry>();
            _defaultClip = "";
            _playOnStart = true;
            _speed = 1f;
        }

        /// <summary>Resolve a clip NAME from the table to its .vanim path; a direct path passes through.</summary>
        public string ResolveClipPath(string nameOrPath)
        {
            if (string.IsNullOrEmpty(nameOrPath)) return null;
            if (_clips != null)
            {
                foreach (var c in _clips)
                    if (string.Equals(c.Name, nameOrPath, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(c.Path))
                        return c.Path;
            }
            return nameOrPath.EndsWith(".vanim", StringComparison.OrdinalIgnoreCase) ? nameOrPath : null;
        }
    }
}
