using CardboardXR.Hands;
using NUnit.Framework;
using UnityEngine;

namespace CardboardXR.Stereo.Tests
{
    public class PalmGestureTests
    {
        // A hand held up in the image (x right, y down): wrist at the bottom, knuckles above it.
        static readonly Vector2 k_Wrist = new(320f, 400f);
        static readonly Vector2 k_ThumbSide = new(370f, 280f);
        static readonly Vector2 k_LittleSide = new(270f, 290f);

        [Test]
        public void ARightPalmTowardTheCameraHasItsThumbSideOnTheRight()
        {
            Assert.Greater(PalmGesture.Facing(k_Wrist, k_ThumbSide, k_LittleSide, rightHand: true), 0.5f);
            // Its back: the index knuckle on the left.
            Assert.Less(PalmGesture.Facing(k_Wrist, k_LittleSide, k_ThumbSide, rightHand: true), -0.5f);
        }

        [Test]
        public void ALeftHandIsTheMirrorImage()
        {
            Assert.Less(PalmGesture.Facing(k_Wrist, k_ThumbSide, k_LittleSide, rightHand: false), -0.5f);
            Assert.Greater(PalmGesture.Facing(k_Wrist, k_LittleSide, k_ThumbSide, rightHand: false), 0.5f);
        }

        [Test]
        public void TurningTheImageDoesNotChangeIt()
        {
            var turn = Quaternion.Euler(0f, 0f, 90f);
            Vector2 Turn(Vector2 p) => turn * (p - k_Wrist);
            var upright = PalmGesture.Facing(k_Wrist, k_ThumbSide, k_LittleSide, true);
            var turned = PalmGesture.Facing(Vector2.zero, Turn(k_ThumbSide), Turn(k_LittleSide), true);
            Assert.AreEqual(upright, turned, 1e-4f);
        }

        [Test]
        public void EdgeOnIsNearZero()
        {
            // Knuckles in one line with the wrist: the palm seen side-on.
            var facing = PalmGesture.Facing(k_Wrist, new Vector2(320f, 280f), new Vector2(321f, 290f), true);
            Assert.Less(Mathf.Abs(facing), 0.1f);
            Assert.AreEqual(0f, PalmGesture.Facing(k_Wrist, k_Wrist, k_LittleSide, true));
        }

        [Test]
        public void TheImageMiddleIsStraightAhead()
        {
            Assert.Less(Vector3.Angle(Vector3.forward, PalmGesture.Ray(new Vector2(0.5f, 0.5f), 64f, 50f)), 0.01f);
            // The right edge is half the view across, to the right; the top is up.
            Assert.AreEqual(32f, Vector3.Angle(Vector3.forward, PalmGesture.Ray(new Vector2(1f, 0.5f), 64f, 50f)), 0.01f);
            Assert.Greater(PalmGesture.Ray(new Vector2(1f, 0.5f), 64f, 50f).x, 0f);
            Assert.Greater(PalmGesture.Ray(new Vector2(0.5f, 0f), 64f, 50f).y, 0f);
        }

        [Test]
        public void ThePalmStateNeedsAMomentAndHasHysteresis()
        {
            var state = new PalmFacingState();
            Assert.IsFalse(state.Update(true, 0.9f, PalmFacingState.k_ShowSeconds / 2f));
            Assert.IsTrue(state.Update(true, 0.9f, PalmFacingState.k_ShowSeconds));
            Assert.IsTrue(state.IsFacing);
            // Between the thresholds it stays shown.
            Assert.IsFalse(state.Update(true, (PalmFacingState.k_Show + PalmFacingState.k_Hide) / 2f, 1f));
            Assert.IsTrue(state.IsFacing);
            // A brief loss doesn't hide it; a longer one does.
            Assert.IsFalse(state.Update(false, 0f, PalmFacingState.k_HideSeconds / 2f));
            Assert.IsTrue(state.IsFacing);
            Assert.IsTrue(state.Update(false, 0f, PalmFacingState.k_HideSeconds));
            Assert.IsFalse(state.IsFacing);
        }

        [Test]
        public void ThePanelFollowsLazily()
        {
            var following = false;
            var speed = 0f;
            const float deadZone = 33f;
            // Looking at a button near its edge: the panel stays put.
            Assert.AreEqual(0f, SettingsSummoner.FollowYaw(0f, 25f, deadZone, ref following, ref speed, 0.1f));
            Assert.IsFalse(following);
            // Past the dead zone it starts turning, gently: a frame in, it has moved only a little.
            var yaw = SettingsSummoner.FollowYaw(0f, 40f, deadZone, ref following, ref speed, 1f / 60f);
            Assert.IsTrue(following);
            Assert.That(yaw, Is.InRange(0.01f, 3f));
            // It gets there in about a second, and stops.
            for (var i = 0; i < 120; i++)
                yaw = SettingsSummoner.FollowYaw(yaw, 40f, deadZone, ref following, ref speed, 1f / 60f);
            Assert.AreEqual(40f, yaw, 1f);
            Assert.IsFalse(following);
            // Across north, the short way round.
            following = false;
            speed = 0f;
            yaw = SettingsSummoner.FollowYaw(350f, 30f, deadZone, ref following, ref speed, 0.1f);
            Assert.That(Mathf.DeltaAngle(350f, yaw), Is.GreaterThan(0f));
        }
    
        [Test]
        public void TurningTheHandOverHoldsBackAPinch()
        {
            var gate = new HandTurnGate();
            // Steady: facing changes slowly.
            Assert.IsFalse(gate.Update(0.6f, 0f));
            Assert.IsFalse(gate.Update(0.62f, 0.04f));
            // Turned from its back to its palm in about a quarter second.
            Assert.IsTrue(gate.Update(0.1f, 0.08f));
            Assert.IsTrue(gate.Update(-0.4f, 0.12f));
            // Still for a moment after the turn ends, then not.
            Assert.IsTrue(gate.Update(-0.42f, 0.12f + HandTurnGate.k_HoldSeconds * 0.5f));
            Assert.IsFalse(gate.Update(-0.42f, 0.12f + HandTurnGate.k_HoldSeconds + 0.05f));
        }

        [Test]
        public void AGapInReadingsIsntATurn()
        {
            var gate = new HandTurnGate();
            gate.Update(0.7f, 0f);
            // The hand was lost for a second and came back the other way round.
            Assert.IsFalse(gate.Update(-0.7f, 1f));
        }
    
        [Test]
        public void TheHandFrameTurnsWithTheHand()
        {
            // A hand held up: fingers up, index to one side of the pinky.
            var wrist = Vector3.zero;
            var index = new Vector3(-0.03f, 0.09f, 0f);
            var middle = new Vector3(0f, 0.1f, 0f);
            var pinky = new Vector3(0.035f, 0.08f, 0f);
            var frame = RotateWithHand.HandFrame(wrist, index, middle, pinky).Value;
            // Turned 90 degrees about the forearm: the frame turns by the same.
            var twist = Quaternion.AngleAxis(90f, Vector3.up);
            var turned = RotateWithHand.HandFrame(twist * wrist, twist * index, twist * middle, twist * pinky).Value;
            Assert.AreEqual(90f, Quaternion.Angle(frame, turned), 0.5f);
            Assert.AreEqual(0f, Quaternion.Angle(twist * frame, turned), 0.5f);
        }

        [Test]
        public void SmallTurnsDontTurnTheObject()
        {
            var small = Quaternion.AngleAxis(RotateWithHand.k_DeadZone * 0.8f, Vector3.up);
            Assert.AreEqual(0f, Quaternion.Angle(Quaternion.identity, RotateWithHand.DeadZoned(small)), 0.01f);
            var middle = Quaternion.AngleAxis(RotateWithHand.k_DeadZone * 1.5f, Vector3.up);
            Assert.AreEqual(RotateWithHand.k_DeadZone, Quaternion.Angle(Quaternion.identity, RotateWithHand.DeadZoned(middle)), 0.1f);
            var big = Quaternion.AngleAxis(90f, Vector3.forward);
            Assert.AreEqual(0f, Quaternion.Angle(big, RotateWithHand.DeadZoned(big)), 0.1f);
        }

        [Test]
        public void ADegenerateHandHasNoFrame()
        {
            Assert.IsNull(RotateWithHand.HandFrame(Vector3.zero, Vector3.zero, Vector3.zero, Vector3.zero));
        }
    }
}
