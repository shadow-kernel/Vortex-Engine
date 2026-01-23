using System;
using System.Runtime.Serialization;

namespace Editor.Core.Assets
{
    /// <summary>
    /// Represents a reference to an asset by GUID.
    /// Used throughout the engine to reference assets without storing paths.
    /// </summary>
    [DataContract]
    public class AssetReference
    {
        [DataMember]
        public Guid Guid { get; set; }

        [DataMember]
        public AssetType Type { get; set; }

        public AssetReference()
        {
            Guid = Guid.Empty;
            Type = AssetType.Unknown;
        }

        public AssetReference(Guid guid, AssetType type)
        {
            Guid = guid;
            Type = type;
        }

        public bool IsValid => Guid != Guid.Empty;

        public override string ToString() => $"{Type}:{Guid}";

        public override bool Equals(object obj)
        {
            return obj is AssetReference other && Guid == other.Guid;
        }

        public override int GetHashCode() => Guid.GetHashCode();
    }
}
