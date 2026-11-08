using System;

namespace CardboardXR.Stereo
{
    // Physical description of a Cardboard-style viewer, mirroring the Cardboard SDK's DeviceParams
    // protobuf (proto/cardboard_device.proto). Lengths are in meters, angles in degrees.
    [Serializable]
    public class ViewerParams
    {
        public enum VerticalAlignment
        {
            Bottom = 0, // phone rests against a fixed bottom tray
            Center = 1, // screen is centered on the lenses
            Top = 2,    // phone rests against a fixed top tray
        }

        public enum ButtonType
        {
            None = 0,
            Magnet = 1,
            Touch = 2,
            IndirectTouch = 3,
        }

        public string vendor = "";
        public string model = "";
        public float screenToLensDistance;
        public float interLensDistance;
        // Left eye's view extent (left, right, bottom, top) from the optical axis. The right eye
        // mirrors left and right.
        public float[] leftEyeFieldOfViewAngles = new float[4];
        public VerticalAlignment verticalAlignment = VerticalAlignment.Bottom;
        public float trayToLensDistance;
        // K1, K2, ... of p' = p (1 + K1 r^2 + K2 r^4 + ...), r in tan-angle units.
        public float[] distortionCoefficients = Array.Empty<float>();
        public ButtonType primaryButton = ButtonType.Magnet;

        // The original 2014 Cardboard. The Cardboard SDK falls back to these when no viewer QR code
        // has been scanned; values decoded from sdk/qrcode/cardboard_v1/cardboard_v1.cc.
        public static ViewerParams CardboardV1 => new()
        {
            vendor = "Google, Inc.",
            model = "Cardboard v1",
            screenToLensDistance = 0.042f,
            interLensDistance = 0.06f,
            leftEyeFieldOfViewAngles = new[] { 40f, 40f, 40f, 40f },
            verticalAlignment = VerticalAlignment.Bottom,
            trayToLensDistance = 0.035f,
            distortionCoefficients = new[] { 0.441f, 0.156f },
            primaryButton = ButtonType.Magnet,
        };
    }
}
