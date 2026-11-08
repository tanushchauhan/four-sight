using System.Runtime.InteropServices;
using System.Text;

namespace CardboardXR.Stereo
{
    // The gyroscope and accelerometer read natively at their fastest rate, with hardware timestamps
    // (Plugins/Android/CardboardXRImu.cpp). Samples are in the phone's own frame, not rotated for the
    // screen, and time comes from NowNs's clock. Only available in Android players.
    public static class NativeImu
    {
        public const int Gyroscope = 0;
        public const int Accelerometer = 1;

        // Matches Sample in CardboardXRImu.cpp.
        [StructLayout(LayoutKind.Sequential)]
        public struct Sample
        {
            public int type;
            int m_Reserved;
            public long timestampNs;
            public float x;
            public float y;
            public float z;
            float m_Reserved2;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        public static bool Supported => true;

        // 0 once both sensors deliver; see CardboardXR_ImuStart for the errors.
        public static int Start(string packageName) => CardboardXR_ImuStart(packageName);
        public static void Stop() => CardboardXR_ImuStop();
        // Moves buffered samples, oldest first, into samples. Returns how many.
        public static int Read(Sample[] samples) => CardboardXR_ImuRead(samples, samples.Length);
        public static long NowNs() => CardboardXR_ImuNowNs();
        public static long Dropped() => CardboardXR_ImuDropped();

        public static string Describe()
        {
            var buffer = new byte[512];
            return Encoding.UTF8.GetString(buffer, 0, CardboardXR_ImuDescribe(buffer, buffer.Length));
        }

        [DllImport("__Internal")] static extern int CardboardXR_ImuStart(string packageName);
        [DllImport("__Internal")] static extern void CardboardXR_ImuStop();
        [DllImport("__Internal")] static extern int CardboardXR_ImuRead([Out] Sample[] samples, int max);
        [DllImport("__Internal")] static extern long CardboardXR_ImuNowNs();
        [DllImport("__Internal")] static extern long CardboardXR_ImuDropped();
        [DllImport("__Internal")] static extern int CardboardXR_ImuDescribe(byte[] buffer, int size);
#else
        public static bool Supported => false;
        public static int Start(string packageName) => -1;
        public static void Stop() { }
        public static int Read(Sample[] samples) => 0;
        public static long NowNs() => 0;
        public static long Dropped() => 0;
        public static string Describe() => "";
#endif
    }
}
