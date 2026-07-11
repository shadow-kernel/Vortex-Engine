using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;
using Editor.Core.Data;
using Editor.ECS;
using Editor.ECS.Components;
using Editor.ECS.Components.Audio;
using Editor.ECS.Components.Lighting;
using Editor.ECS.Components.Physics;
using Editor.ECS.Components.Rendering;
using Editor.ECS.Components.Scripting;

namespace Editor.Core.Serialization
{
    /// <summary>
    /// Zentrale Serialisierungs-Klasse die sowohl JSON als auch Bin�r unterst�tzt.
    /// Verwendet DataContractSerializer f�r konsistente Serialisierung.
    /// </summary>
    public static class DataSerializer
    {
        /// <summary>
        /// Liste aller bekannten Typen f�r polymorphe Serialisierung
        /// </summary>
        private static readonly List<Type> KnownTypes = new List<Type>
        {
            // Core Data
            typeof(Scene),
            typeof(ProjectManifest),
            typeof(SceneReference),
            typeof(ProjectSettings),
            
            // ECS
            typeof(GameEntity),
            typeof(Component),
            
            // Transform
            typeof(Transform),
            typeof(Vector3),
            typeof(Quaternion),
            
            // Rendering
            typeof(MeshRenderer),
            typeof(SpriteRenderer),
            typeof(Camera),
            typeof(Skybox),
            
            // Lighting
            typeof(Light),
            
            // Physics
            typeof(Collider),
            typeof(BoxCollider),
            typeof(SphereCollider),
            typeof(CapsuleCollider),
            typeof(MeshCollider),
            typeof(Rigidbody),
            typeof(PhysicsMaterial),
            
            // Audio
            typeof(AudioSource),
            typeof(AudioListener),
            typeof(ReverbZone),

            // Animation
            typeof(Editor.ECS.Components.Animation.Animator),
            typeof(Editor.ECS.Components.Animation.AnimatorClipEntry),
            typeof(Editor.ECS.Components.Animation.BoneAttachment),
            typeof(Editor.ECS.Components.Animation.TwoBoneIk),

            // Scripting
            typeof(Script),
            typeof(Editor.ECS.Components.Scripting.ScriptFieldValue)   // #47: serialized public fields
        };

        /// <summary>
        /// Serialisiert ein Objekt zu JSON
        /// </summary>
        public static string ToJson<T>(T obj) where T : class
        {
            if (obj == null)
                throw new ArgumentNullException(nameof(obj));

            var serializer = new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings
            {
                UseSimpleDictionaryFormat = true,
                EmitTypeInformation = EmitTypeInformation.AsNeeded,
                KnownTypes = KnownTypes
            });

            using (var stream = new MemoryStream())
            {
                using (var writer = JsonReaderWriterFactory.CreateJsonWriter(stream, Encoding.UTF8, true, true, "  "))
                {
                    serializer.WriteObject(writer, obj);
                    writer.Flush();
                }
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        /// <summary>
        /// Deserialisiert JSON zu einem Objekt
        /// </summary>
        public static T FromJson<T>(string json) where T : class
        {
            if (string.IsNullOrEmpty(json))
                throw new ArgumentNullException(nameof(json));

            var serializer = new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings
            {
                UseSimpleDictionaryFormat = true,
                KnownTypes = KnownTypes
            });

            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                return (T)serializer.ReadObject(stream);
            }
        }

        /// <summary>
        /// Serialisiert ein Objekt zu Bin�rdaten
        /// </summary>
        public static byte[] ToBinary<T>(T obj) where T : class
        {
            if (obj == null)
                throw new ArgumentNullException(nameof(obj));

            var serializer = new DataContractSerializer(typeof(T), KnownTypes);
            using (var stream = new MemoryStream())
            {
                using (var writer = XmlDictionaryWriter.CreateBinaryWriter(stream))
                {
                    serializer.WriteObject(writer, obj);
                }
                return stream.ToArray();
            }
        }

        /// <summary>
        /// Deserialisiert Bin�rdaten zu einem Objekt
        /// </summary>
        public static T FromBinary<T>(byte[] data) where T : class
        {
            if (data == null || data.Length == 0)
                throw new ArgumentNullException(nameof(data));

            var serializer = new DataContractSerializer(typeof(T), KnownTypes);
            using (var stream = new MemoryStream(data))
            using (var reader = XmlDictionaryReader.CreateBinaryReader(stream, XmlDictionaryReaderQuotas.Max))
            {
                return (T)serializer.ReadObject(reader);
            }
        }

        /// <summary>
        /// Speichert ein Objekt als JSON-Datei
        /// </summary>
        public static void SaveAsJson<T>(T obj, string filePath) where T : class
        {
            var json = ToJson(obj);
            File.WriteAllText(filePath, json, Encoding.UTF8);
        }

        /// <summary>
        /// L�dt ein Objekt aus einer JSON-Datei
        /// </summary>
        public static T LoadFromJson<T>(string filePath) where T : class
        {
            // Shipped game: read from the in-RAM asset pak; editor: read the loose file.
            string json = (Editor.Core.Services.AssetVfs.IsMounted && Editor.Core.Services.AssetVfs.Contains(filePath))
                ? Editor.Core.Services.AssetVfs.GetText(filePath)
                : File.ReadAllText(filePath, Encoding.UTF8);
            return FromJson<T>(json);
        }

        /// <summary>
        /// Speichert ein Objekt als Bin�rdatei
        /// </summary>
        public static void SaveAsBinary<T>(T obj, string filePath) where T : class
        {
            var data = ToBinary(obj);
            File.WriteAllBytes(filePath, data);
        }

        /// <summary>
        /// L�dt ein Objekt aus einer Bin�rdatei
        /// </summary>
        public static T LoadFromBinary<T>(string filePath) where T : class
        {
            byte[] data;
            if (Editor.Core.Services.AssetVfs.IsMounted && Editor.Core.Services.AssetVfs.TryGetBytes(filePath, out var packed))
                data = packed;
            else
                data = File.ReadAllBytes(filePath);
            return FromBinary<T>(data);
        }
    }
}
