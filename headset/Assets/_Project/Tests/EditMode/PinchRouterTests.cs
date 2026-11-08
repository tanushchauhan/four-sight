using CardboardXR.Hands;
using NUnit.Framework;

namespace CardboardXR.Stereo.Tests
{
    public class PinchRouterTests
    {
        static PinchRoute Route(bool settingsButton = false, bool settingsOpen = false, bool onButton = false,
            bool? demoTakes = null, bool canGrab = false, bool onHandOnlyObject = false) =>
            PinchRouter.Order(settingsButton, settingsOpen, onButton, demoTakes.HasValue ? () => demoTakes.Value : null,
                canGrab, onHandOnlyObject);

        [Test]
        public void TheSettingsGearComesFirst()
        {
            Assert.AreEqual(PinchRoute.Settings, Route(settingsButton: true, onButton: true, demoTakes: true, canGrab: true));
        }

        [Test]
        public void AButtonBeatsTheDemoAndGrabbing()
        {
            Assert.AreEqual(PinchRoute.Button, Route(onButton: true, demoTakes: true, canGrab: true));
        }

        [Test]
        public void TheDemoIsAskedOnlyOnItsTurn()
        {
            var asked = false;
            PinchRouter.Order(false, false, true, () => asked = true, false, false);
            Assert.IsFalse(asked, "asked although a button took the pinch");
            Assert.AreEqual(PinchRoute.Demo, Route(demoTakes: true, canGrab: true));
        }

        [Test]
        public void DemosDontTakePinchesWhileSettingsAreOpen()
        {
            Assert.AreEqual(PinchRoute.Click, Route(settingsOpen: true, demoTakes: true));
        }

        [Test]
        public void ADeclinedPinchGrabsThenClicks()
        {
            Assert.AreEqual(PinchRoute.Grab, Route(demoTakes: false, canGrab: true));
            Assert.AreEqual(PinchRoute.Click, Route(demoTakes: false));
            Assert.AreEqual(PinchRoute.Click, Route());
        }

        [Test]
        public void APlacedObjectIsntClickedByAHandPinch()
        {
            Assert.AreEqual(PinchRoute.Ignored, Route(onHandOnlyObject: true));
            Assert.AreEqual(PinchRoute.Grab, Route(onHandOnlyObject: true, canGrab: true));
        }

        [Test]
        public void ARepeatTooSoonAfterAClickDoesNothing()
        {
            Assert.AreEqual(PinchRoute.Ignored, PinchRouter.Order(true, false, false, null, false, false, lockedOut: true));
            Assert.AreEqual(PinchRoute.Ignored, PinchRouter.Order(false, false, true, () => true, true, false, lockedOut: true));
            Assert.AreEqual(PinchRoute.Ignored, PinchRouter.Order(false, false, false, null, false, false, lockedOut: true));
            // Painting and grabbing aren't clicks.
            Assert.AreEqual(PinchRoute.Demo, PinchRouter.Order(false, false, false, () => true, false, false, lockedOut: true));
            Assert.AreEqual(PinchRoute.Grab, PinchRouter.Order(false, false, false, null, true, false, lockedOut: true));
        }

        [Test]
        public void TheHandHintNeedsAButtonAndNoHand()
        {
            var t = PinchInput.k_HintSeconds;
            Assert.IsTrue(PinchInput.ShowHint(true, t, t));
            Assert.IsFalse(PinchInput.ShowHint(false, t, t), "pinching off");
            Assert.IsFalse(PinchInput.ShowHint(true, 0f, t), "hand in view");
            Assert.IsFalse(PinchInput.ShowHint(true, t, 0.5f * t), "only glanced at the button");
        }
    }
}
