using System;

namespace CardboardXR.Tracking
{
    // Double-precision 3D vector for sensor fusion, where float precision loses small rotations.
    // Ported from the Cardboard SDK (sdk/util/vector.h and vectorutils.cc, Apache License 2.0).
    public struct Vector3d
    {
        public double x;
        public double y;
        public double z;

        public Vector3d(double x, double y, double z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public static Vector3d Zero => default;

        public double this[int index]
        {
            get => index switch
            {
                0 => x,
                1 => y,
                2 => z,
                _ => throw new ArgumentOutOfRangeException(nameof(index)),
            };
            set
            {
                switch (index)
                {
                    case 0: x = value; break;
                    case 1: y = value; break;
                    case 2: z = value; break;
                    default: throw new ArgumentOutOfRangeException(nameof(index));
                }
            }
        }

        public double LengthSquared => x * x + y * y + z * z;

        public double Length => Math.Sqrt(LengthSquared);

        // The unit vector, or zero for a zero vector.
        public Vector3d Normalized
        {
            get
            {
                var length = Length;
                return length == 0 ? Zero : this / length;
            }
        }

        public static double Dot(Vector3d a, Vector3d b) => a.x * b.x + a.y * b.y + a.z * b.z;

        public static Vector3d Cross(Vector3d a, Vector3d b) =>
            new(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);

        public static Vector3d operator +(Vector3d a, Vector3d b) => new(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3d operator -(Vector3d a, Vector3d b) => new(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3d operator -(Vector3d v) => new(-v.x, -v.y, -v.z);
        public static Vector3d operator *(Vector3d v, double s) => new(v.x * s, v.y * s, v.z * s);
        public static Vector3d operator *(double s, Vector3d v) => v * s;
        public static Vector3d operator /(Vector3d v, double s) => new(v.x / s, v.y / s, v.z / s);

        public override string ToString() => $"({x:F5}, {y:F5}, {z:F5})";
    }
}
