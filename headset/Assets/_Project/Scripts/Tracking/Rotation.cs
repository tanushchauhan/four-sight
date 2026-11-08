using System;

namespace CardboardXR.Tracking
{
    // A 3D rotation stored as a normalized double-precision quaternion (x, y, z vector part, w
    // scalar), with right-hand-rule axis-angle conventions and Hamilton products.
    // Ported from the Cardboard SDK (sdk/util/rotation.h and rotation.cc, Apache License 2.0).
    public readonly struct Rotation
    {
        public readonly double x;
        public readonly double y;
        public readonly double z;
        public readonly double w;

        Rotation(double x, double y, double z, double w)
        {
            this.x = x;
            this.y = y;
            this.z = z;
            this.w = w;
        }

        public static Rotation Identity => new(0, 0, 0, 1);

        // Normalizes the quaternion, like the SDK's SetQuaternion.
        public static Rotation FromQuaternion(double x, double y, double z, double w)
        {
            var length = Math.Sqrt(x * x + y * y + z * z + w * w);
            return length == 0 ? new Rotation(0, 0, 0, 0) : new Rotation(x / length, y / length, z / length, w / length);
        }

        // Rotates by angle radians around axis, which needn't be unit length. A zero axis gives the
        // identity.
        public static Rotation FromAxisAndAngle(Vector3d axis, double angle)
        {
            var length = axis.Length;
            if (length == 0)
                return Identity;
            var unitAxis = axis / length;
            var s = Math.Sin(angle / 2);
            return FromQuaternion(unitAxis.x * s, unitAxis.y * s, unitAxis.z * s, Math.Cos(angle / 2));
        }

        // The right-hand-rule axis and angle. The identity gives the +X axis and an angle of 0.
        public void GetAxisAndAngle(out Vector3d axis, out double angle)
        {
            var vector = new Vector3d(x, y, z);
            var length = vector.Length;
            if (length == 0)
            {
                axis = new Vector3d(1, 0, 0);
                angle = 0;
                return;
            }

            axis = vector / length;
            // Clamped because rounding can push |w| a hair past 1, where acos is undefined.
            angle = 2 * Math.Acos(Math.Clamp(w, -1.0, 1.0));
        }

        // The shortest-arc rotation that turns one direction into another; identity if either is zero.
        public static Rotation RotateInto(Vector3d from, Vector3d to)
        {
            // std::numeric_limits<double>::epsilon() * 100; C#'s double.Epsilon is the smallest
            // subnormal, not machine epsilon.
            const double tolerance = 2.220446049250313e-16 * 100;
            // http://lolengine.net/blog/2014/02/24/quaternion-from-two-vectors-final
            var normProduct = Math.Sqrt(from.LengthSquared * to.LengthSquared);
            var realPart = normProduct + Vector3d.Dot(from, to);
            Vector3d w;
            if (realPart < tolerance * normProduct)
            {
                // Opposite directions: turn 180 degrees around any axis orthogonal to from.
                realPart = 0;
                w = Math.Abs(from.x) > Math.Abs(from.z) ? new Vector3d(-from.y, from.x, 0) : new Vector3d(0, -from.z, from.y);
            }
            else
            {
                w = Vector3d.Cross(from, to);
            }

            return FromQuaternion(w.x, w.y, w.z, realPart);
        }

        public Rotation Inverse => new(-x, -y, -z, w);

        // Hamilton product: applying r1 first, then r0, to a vector.
        public static Rotation operator *(Rotation r0, Rotation r1) => FromQuaternion(
            r0.w * r1.x + r0.x * r1.w + r0.y * r1.z - r0.z * r1.y,
            r0.w * r1.y + r0.y * r1.w + r0.z * r1.x - r0.x * r1.z,
            r0.w * r1.z + r0.z * r1.w + r0.x * r1.y - r0.y * r1.x,
            r0.w * r1.w - r0.x * r1.x - r0.y * r1.y - r0.z * r1.z);

        public static Vector3d operator *(Rotation r, Vector3d v)
        {
            // http://blog.molecular-matters.com/2013/05/24/a-faster-quaternion-vector-multiplication/
            var im = new Vector3d(r.x, r.y, r.z);
            var temp = 2.0 * Vector3d.Cross(im, v);
            return v + r.w * temp + Vector3d.Cross(im, temp);
        }

        public override string ToString() => $"({x:F5}, {y:F5}, {z:F5}, {w:F5})";
    }
}
