using UnityEngine;

namespace CardboardXR.Stereo
{
    // Radial lens distortion p' = p (1 + K1 r^2 + K2 r^4 + ...), with r in tan-angle units: distance
    // on the screen divided by the distance from the eye to the screen.
    // Ported from the Cardboard SDK (sdk/polynomial_radial_distortion.cc, Apache License 2.0).
    public sealed class PolynomialRadialDistortion
    {
        const int k_MaxInverseIterations = 100;

        readonly float[] m_Coefficients;

        public PolynomialRadialDistortion(float[] coefficients)
        {
            m_Coefficients = (float[])coefficients.Clone();
        }

        public float DistortionFactor(float radiusSquared)
        {
            var radiusFactor = 1f;
            var distortionFactor = 1f;
            foreach (var coefficient in m_Coefficients)
            {
                radiusFactor *= radiusSquared;
                distortionFactor += coefficient * radiusFactor;
            }
            return distortionFactor;
        }

        public float DistortRadius(float radius) => radius * DistortionFactor(radius * radius);

        public Vector2 Distort(Vector2 point) => point * DistortionFactor(point.sqrMagnitude);

        // Solves Distort(result) = point for the radius with the secant method, like the SDK. The
        // iteration cap only guards against coefficients that never converge.
        public Vector2 DistortInverse(Vector2 point)
        {
            var radius = point.magnitude;
            if (radius < float.Epsilon)
                return Vector2.zero;

            var r0 = radius / 2f;
            var r1 = radius / 3f;
            var dr0 = radius - DistortRadius(r0);
            for (var i = 0; i < k_MaxInverseIterations && Mathf.Abs(r1 - r0) > 0.0001f; i++)
            {
                var dr1 = radius - DistortRadius(r1);
                var r2 = r1 - dr1 * ((r1 - r0) / (dr1 - dr0));
                r0 = r1;
                r1 = r2;
                dr0 = dr1;
            }

            return point * (r1 / radius);
        }
    }
}
