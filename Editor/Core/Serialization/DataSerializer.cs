using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;

namespace Editor.Core.Serialization
{
    /// <summary>
    /// Zentrale Serialisierungs-Klasse die sowohl JSON als auch Binär unterstützt.
    /// Verwendet DataContractSerializer für konsistente Serialisierung.
    /// </summary>
    public static class DataSerializer
    {
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
                EmitTypeInformation = EmitTypeInformation.Never
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
                UseSimpleDictionaryFormat = true
            });

            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                return (T)serializer.ReadObject(stream);
            }
        }

        /// <summary>
        /// Serialisiert ein Objekt zu Binärdaten
        /// </summary>
        public static byte[] ToBinary<T>(T obj) where T : class
        {
            if (obj == null)
                throw new ArgumentNullException(nameof(obj));

            var serializer = new DataContractSerializer(typeof(T));
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
        /// Deserialisiert Binärdaten zu einem Objekt
        /// </summary>
        public static T FromBinary<T>(byte[] data) where T : class
        {
            if (data == null || data.Length == 0)
                throw new ArgumentNullException(nameof(data));

            var serializer = new DataContractSerializer(typeof(T));
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
        /// Lädt ein Objekt aus einer JSON-Datei
        /// </summary>
        public static T LoadFromJson<T>(string filePath) where T : class
        {
            var json = File.ReadAllText(filePath, Encoding.UTF8);
            return FromJson<T>(json);
        }

        /// <summary>
        /// Speichert ein Objekt als Binärdatei
        /// </summary>
        public static void SaveAsBinary<T>(T obj, string filePath) where T : class
        {
            var data = ToBinary(obj);
            File.WriteAllBytes(filePath, data);
        }

        /// <summary>
        /// Lädt ein Objekt aus einer Binärdatei
        /// </summary>
        public static T LoadFromBinary<T>(string filePath) where T : class
        {
            var data = File.ReadAllBytes(filePath);
            return FromBinary<T>(data);
        }
    }
}
