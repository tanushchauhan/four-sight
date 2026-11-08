using UnityEngine;

namespace CardboardXR.Hands
{
    // Whether a hand shows its palm to the camera, from three of its landmarks in the camera image
    // and which hand it is. The phone's camera sees about what the eyes see, so a palm toward the
    // camera is a palm the user is looking at, as visionOS uses to offer its controls.
    //   In an image with x right and y down, a right hand held up with its palm to the camera has its
    //   index knuckle to the right of the wrist-to-pinky line, which makes the 2D cross product of
    //   (index knuckle - wrist) and (pinky knuckle - wrist) negative; its back, or a left palm,
    //   flips the sign. Turning the image doesn't change the sign; only mirroring would, and neither
    //   hand pipeline's images are mirrored. k_Sign is here in case the device says otherwise.
    public static class PalmGesture
    {
        public const int k_Wrist = 0;
        public const int k_IndexKnuckle = 5;
        public const int k_PinkyKnuckle = 17;
        public const float k_Sign = 1f;

        // How squarely the palm faces the camera, from -1 (its back does) to 1, as the sine of the
        // angle between the wrist's lines to the index and pinky knuckles in the image: a palm seen
        // edge-on squeezes them together toward 0.
        public static float Facing(Vector2 wrist, Vector2 indexKnuckle, Vector2 pinkyKnuckle, bool rightHand)
        {
            var toIndex = indexKnuckle - wrist;
            var toPinky = pinkyKnuckle - wrist;
            var lengths = toIndex.magnitude * toPinky.magnitude;
            if (lengths < 1e-6f)
                return 0f;
            var cross = toIndex.x * toPinky.y - toIndex.y * toPinky.x;
            var facing = -cross / lengths * k_Sign;
            return rightHand ? facing : -facing;
        }

        // A direction from a camera, in its own space (x right, y up, z forward), toward a point in its
        // image given as 0-1 across and down, for a camera seeing the given angles across and up.
        public static Vector3 Ray(Vector2 imageFraction, float horizontalDegrees, float verticalDegrees)
        {
            var x = (imageFraction.x - 0.5f) * 2f * Mathf.Tan(horizontalDegrees * 0.5f * Mathf.Deg2Rad);
            var y = (0.5f - imageFraction.y) * 2f * Mathf.Tan(verticalDegrees * 0.5f * Mathf.Deg2Rad);
            return new Vector3(x, y, 1f).normalized;
        }
    }

    // Turns a stream of palm-facing readings into a steady shown or hidden state: shown once the palm
    // has faced the camera clearly (above k_Show) for k_ShowSeconds, hidden once it hasn't (below
    // k_Hide, or the hand gone) for k_HideSeconds. The gap between the two keeps it from flickering
    // as the hand turns.
    public class PalmFacingState
    {
        public const float k_Show = 0.45f;
        public const float k_Hide = 0.2f;
        public const float k_ShowSeconds = 0.25f;
        public const float k_HideSeconds = 0.3f;

        float m_For;

        public bool IsFacing { get; private set; }

        // A reading: whether a hand is seen, how squarely its palm faces (PalmGesture.Facing), and the
        // time since the last. Returns whether the state changed.
        public bool Update(bool handSeen, float facing, float deltaTime)
        {
            var wants = IsFacing ? handSeen && facing > k_Hide : handSeen && facing > k_Show;
            if (wants == IsFacing)
            {
                m_For = 0f;
                return false;
            }
            m_For += deltaTime;
            if (m_For < (IsFacing ? k_HideSeconds : k_ShowSeconds))
                return false;
            IsFacing = wants;
            m_For = 0f;
            return true;
        }

        public void Reset()
        {
            IsFacing = false;
            m_For = 0f;
        }
    }

    // Whether the hand is turning over, from how fast its palm-facing reading (PalmGesture.Facing)
    // changes: a pinch shouldn't start then. An open hand turned from its back to its palm passes
    // edge-on, where the hand model squeezes the thumb and index fingertips together for a few
    // frames, and that read as a pinch: on the Pixel, turning the hand to summon settings clicked
    // Apps seven times in six seconds. A real pinch keeps the hand's turn steady. Turning counts from
    // k_TurnRate (facing per second, which runs -1 to 1) and for k_HoldSeconds after, since the false
    // pinch can come just as the turn ends.
    public class HandTurnGate
    {
        public const float k_TurnRate = 2f;
        public const float k_HoldSeconds = 0.25f;

        float m_LastFacing;
        float m_LastTime = float.NegativeInfinity;
        float m_TurningUntil = float.NegativeInfinity;

        public bool IsTurning { get; private set; }

        // A reading of the facing at a time, in seconds; returns whether the hand is turning.
        public bool Update(float facing, float time)
        {
            var interval = time - m_LastTime;
            if (interval > 0f && interval < 0.5f && Mathf.Abs(facing - m_LastFacing) / interval > k_TurnRate)
                m_TurningUntil = time + k_HoldSeconds;
            m_LastFacing = facing;
            m_LastTime = time;
            IsTurning = time < m_TurningUntil;
            return IsTurning;
        }

        public void Reset()
        {
            m_LastTime = float.NegativeInfinity;
            m_TurningUntil = float.NegativeInfinity;
            IsTurning = false;
        }
    }
}
