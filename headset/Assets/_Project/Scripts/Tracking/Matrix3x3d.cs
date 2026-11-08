namespace CardboardXR.Tracking
{
    // Double-precision 3x3 matrix for the Kalman filter's covariances.
    // Ported from the Cardboard SDK (sdk/util/matrix_3x3.cc and matrixutils.cc, Apache License 2.0).
    public struct Matrix3x3d
    {
        double m00, m01, m02, m10, m11, m12, m20, m21, m22;

        public static Matrix3x3d Zero => default;

        public static Matrix3x3d Identity => new() { m00 = 1, m11 = 1, m22 = 1 };

        public double this[int row, int col]
        {
            get => (row * 3 + col) switch
            {
                0 => m00, 1 => m01, 2 => m02,
                3 => m10, 4 => m11, 5 => m12,
                6 => m20, 7 => m21, 8 => m22,
                _ => throw new System.ArgumentOutOfRangeException(),
            };
            set
            {
                switch (row * 3 + col)
                {
                    case 0: m00 = value; break;
                    case 1: m01 = value; break;
                    case 2: m02 = value; break;
                    case 3: m10 = value; break;
                    case 4: m11 = value; break;
                    case 5: m12 = value; break;
                    case 6: m20 = value; break;
                    case 7: m21 = value; break;
                    case 8: m22 = value; break;
                    default: throw new System.ArgumentOutOfRangeException();
                }
            }
        }

        public Matrix3x3d Transpose()
        {
            var result = new Matrix3x3d();
            for (var row = 0; row < 3; row++)
            {
                for (var col = 0; col < 3; col++)
                    result[row, col] = this[col, row];
            }
            return result;
        }

        // The adjugate divided by the determinant; zero for a singular matrix.
        public Matrix3x3d Inverse()
        {
            var cofactors = new Matrix3x3d();
            for (var row = 0; row < 3; row++)
            {
                for (var col = 0; col < 3; col++)
                    cofactors[row, col] = Cofactor(row, col);
            }

            var determinant = m00 * cofactors[0, 0] + m01 * cofactors[0, 1] + m02 * cofactors[0, 2];
            return determinant == 0 ? Zero : cofactors.Transpose() * (1.0 / determinant);
        }

        double Cofactor(int row, int col)
        {
            var i0 = row == 0 ? 1 : 0;
            var i1 = row == 2 ? 1 : 2;
            var j0 = col == 0 ? 1 : 0;
            var j1 = col == 2 ? 1 : 2;
            var minor = this[i0, j0] * this[i1, j1] - this[i0, j1] * this[i1, j0];
            return ((row + col) & 1) != 0 ? -minor : minor;
        }

        public static Matrix3x3d FromRotation(Rotation r)
        {
            double a = r.x, b = r.y, c = r.z, d = r.w;
            return new Matrix3x3d
            {
                m00 = a * a - b * b - c * c + d * d,
                m01 = 2 * a * b - 2 * c * d,
                m02 = 2 * a * c + 2 * b * d,
                m10 = 2 * a * b + 2 * c * d,
                m11 = -a * a + b * b - c * c + d * d,
                m12 = 2 * b * c - 2 * a * d,
                m20 = 2 * a * c - 2 * b * d,
                m21 = 2 * b * c + 2 * a * d,
                m22 = -a * a - b * b + c * c + d * d,
            };
        }

        public static Matrix3x3d operator +(Matrix3x3d a, Matrix3x3d b)
        {
            var result = new Matrix3x3d();
            for (var i = 0; i < 9; i++)
                result[i / 3, i % 3] = a[i / 3, i % 3] + b[i / 3, i % 3];
            return result;
        }

        public static Matrix3x3d operator -(Matrix3x3d a, Matrix3x3d b)
        {
            var result = new Matrix3x3d();
            for (var i = 0; i < 9; i++)
                result[i / 3, i % 3] = a[i / 3, i % 3] - b[i / 3, i % 3];
            return result;
        }

        public static Matrix3x3d operator *(Matrix3x3d m, double s)
        {
            var result = new Matrix3x3d();
            for (var i = 0; i < 9; i++)
                result[i / 3, i % 3] = m[i / 3, i % 3] * s;
            return result;
        }

        public static Matrix3x3d operator *(double s, Matrix3x3d m) => m * s;

        public static Matrix3x3d operator *(Matrix3x3d a, Matrix3x3d b)
        {
            var result = new Matrix3x3d();
            for (var row = 0; row < 3; row++)
            {
                for (var col = 0; col < 3; col++)
                {
                    double sum = 0;
                    for (var i = 0; i < 3; i++)
                        sum += a[row, i] * b[i, col];
                    result[row, col] = sum;
                }
            }
            return result;
        }

        public static Vector3d operator *(Matrix3x3d m, Vector3d v) => new(
            m.m00 * v.x + m.m01 * v.y + m.m02 * v.z,
            m.m10 * v.x + m.m11 * v.y + m.m12 * v.z,
            m.m20 * v.x + m.m21 * v.y + m.m22 * v.z);
    }
}
