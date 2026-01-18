using System;
using System.Runtime.Serialization;

namespace Editor.ECS
{
    /// <summary>
    /// 3D-Vektor für Position, Rotation und Skalierung
    /// </summary>
    [DataContract(Name = "Vector3", Namespace = "")]
    public struct Vector3
    {
        [DataMember(Name = "x", Order = 0)]
        public float X { get; set; }

        [DataMember(Name = "y", Order = 1)]
        public float Y { get; set; }

        [DataMember(Name = "z", Order = 2)]
        public float Z { get; set; }

        public static Vector3 Zero => new Vector3(0, 0, 0);
        public static Vector3 One => new Vector3(1, 1, 1);
        public static Vector3 Up => new Vector3(0, 1, 0);
        public static Vector3 Down => new Vector3(0, -1, 0);
        public static Vector3 Forward => new Vector3(0, 0, 1);
        public static Vector3 Back => new Vector3(0, 0, -1);
        public static Vector3 Right => new Vector3(1, 0, 0);
        public static Vector3 Left => new Vector3(-1, 0, 0);

        public Vector3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public Vector3(float value) : this(value, value, value) { }

        public float Magnitude => (float)Math.Sqrt(X * X + Y * Y + Z * Z);
        public float SqrMagnitude => X * X + Y * Y + Z * Z;

        public Vector3 Normalized
        {
            get
            {
                float mag = Magnitude;
                if (mag > 0)
                    return new Vector3(X / mag, Y / mag, Z / mag);
                return Zero;
            }
        }

        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vector3 operator *(Vector3 a, float d) => new Vector3(a.X * d, a.Y * d, a.Z * d);
        public static Vector3 operator *(float d, Vector3 a) => new Vector3(a.X * d, a.Y * d, a.Z * d);
        public static Vector3 operator /(Vector3 a, float d) => new Vector3(a.X / d, a.Y / d, a.Z / d);
        public static Vector3 operator -(Vector3 a) => new Vector3(-a.X, -a.Y, -a.Z);

        public static float Dot(Vector3 a, Vector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        public static Vector3 Cross(Vector3 a, Vector3 b) => new Vector3(
            a.Y * b.Z - a.Z * b.Y,
            a.Z * b.X - a.X * b.Z,
            a.X * b.Y - a.Y * b.X
        );

        public static float Distance(Vector3 a, Vector3 b) => (a - b).Magnitude;

        public static Vector3 Lerp(Vector3 a, Vector3 b, float t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return new Vector3(
                a.X + (b.X - a.X) * t,
                a.Y + (b.Y - a.Y) * t,
                a.Z + (b.Z - a.Z) * t
            );
        }

        public override string ToString() => $"({X:F2}, {Y:F2}, {Z:F2})";

        public override bool Equals(object obj)
        {
            if (obj is Vector3 other)
            {
                return Math.Abs(X - other.X) < float.Epsilon &&
                       Math.Abs(Y - other.Y) < float.Epsilon &&
                       Math.Abs(Z - other.Z) < float.Epsilon;
            }
            return false;
        }

        public override int GetHashCode() => X.GetHashCode() ^ (Y.GetHashCode() << 2) ^ (Z.GetHashCode() >> 2);

        public static bool operator ==(Vector3 a, Vector3 b) => a.Equals(b);
        public static bool operator !=(Vector3 a, Vector3 b) => !a.Equals(b);
    }

    /// <summary>
    /// Quaternion für Rotationen
    /// </summary>
    [DataContract(Name = "Quaternion", Namespace = "")]
    public struct Quaternion
    {
        [DataMember(Name = "x", Order = 0)]
        public float X { get; set; }

        [DataMember(Name = "y", Order = 1)]
        public float Y { get; set; }

        [DataMember(Name = "z", Order = 2)]
        public float Z { get; set; }

        [DataMember(Name = "w", Order = 3)]
        public float W { get; set; }

        public static Quaternion Identity => new Quaternion(0, 0, 0, 1);

        public Quaternion(float x, float y, float z, float w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        /// <summary>
        /// Erstellt eine Quaternion aus Euler-Winkeln (in Grad)
        /// </summary>
        public static Quaternion Euler(float x, float y, float z)
        {
            x *= (float)(Math.PI / 180.0) * 0.5f;
            y *= (float)(Math.PI / 180.0) * 0.5f;
            z *= (float)(Math.PI / 180.0) * 0.5f;

            float sinX = (float)Math.Sin(x);
            float cosX = (float)Math.Cos(x);
            float sinY = (float)Math.Sin(y);
            float cosY = (float)Math.Cos(y);
            float sinZ = (float)Math.Sin(z);
            float cosZ = (float)Math.Cos(z);

            return new Quaternion(
                sinX * cosY * cosZ - cosX * sinY * sinZ,
                cosX * sinY * cosZ + sinX * cosY * sinZ,
                cosX * cosY * sinZ - sinX * sinY * cosZ,
                cosX * cosY * cosZ + sinX * sinY * sinZ
            );
        }

        public static Quaternion Euler(Vector3 euler) => Euler(euler.X, euler.Y, euler.Z);

        /// <summary>
        /// Konvertiert die Quaternion zu Euler-Winkeln (in Grad)
        /// </summary>
        public Vector3 EulerAngles
        {
            get
            {
                float sinrCosp = 2 * (W * X + Y * Z);
                float cosrCosp = 1 - 2 * (X * X + Y * Y);
                float roll = (float)Math.Atan2(sinrCosp, cosrCosp);

                float sinp = 2 * (W * Y - Z * X);
                float pitch = Math.Abs(sinp) >= 1 
                    ? (float)(Math.Sign(sinp) * Math.PI / 2) 
                    : (float)Math.Asin(sinp);

                float sinyCosp = 2 * (W * Z + X * Y);
                float cosyCosp = 1 - 2 * (Y * Y + Z * Z);
                float yaw = (float)Math.Atan2(sinyCosp, cosyCosp);

                return new Vector3(
                    roll * (float)(180.0 / Math.PI),
                    pitch * (float)(180.0 / Math.PI),
                    yaw * (float)(180.0 / Math.PI)
                );
            }
        }

        public static Quaternion operator *(Quaternion a, Quaternion b)
        {
            return new Quaternion(
                a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
                a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
                a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
                a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z
            );
        }

        public override string ToString() => $"({X:F2}, {Y:F2}, {Z:F2}, {W:F2})";
    }
}
