using CardboardXR.Demos;
using NUnit.Framework;

namespace CardboardXR.Stereo.Tests
{
    public class DemoRequirementsTests
    {
        static readonly CoreState k_ThreeDof = new(false, false, true);
        static readonly CoreState k_Passthrough = new(true, true, true);

        [Test]
        public void AnyLeavesEverythingAsItWas()
        {
            Assert.AreEqual(k_ThreeDof, CoreState.Resolve(k_ThreeDof, default));
            Assert.AreEqual(k_Passthrough, CoreState.Resolve(k_Passthrough, default));
        }

        [Test]
        public void PassthroughTurnsSixDofOn()
        {
            var state = CoreState.Resolve(k_ThreeDof, new DemoRequirements { Passthrough = Need.On });
            Assert.AreEqual(new CoreState(true, true, true), state);
        }

        [Test]
        public void HandsTurnSixDofOn()
        {
            var state = CoreState.Resolve(new CoreState(false, false, false), new DemoRequirements { Hands = Need.On });
            Assert.AreEqual(new CoreState(true, false, true), state);
        }

        [Test]
        public void SixDofOffTurnsPassthroughOff()
        {
            var state = CoreState.Resolve(k_Passthrough, new DemoRequirements { SixDof = Need.Off });
            Assert.AreEqual(new CoreState(false, false, true), state);
        }

        [Test]
        public void SixDofOffWinsOverPassthroughOn()
        {
            var state = CoreState.Resolve(k_ThreeDof, new DemoRequirements { SixDof = Need.Off, Passthrough = Need.On });
            Assert.AreEqual(new CoreState(false, false, true), state);
        }

        [Test]
        public void VideoLikeDemoTurnsHandsAndCameraOff()
        {
            var needs = new DemoRequirements { SixDof = Need.Off, Passthrough = Need.Off, Hands = Need.Off };
            Assert.AreEqual(new CoreState(false, false, false), CoreState.Resolve(k_Passthrough, needs));
        }

        [Test]
        public void PassthroughOffKeepsSixDof()
        {
            var state = CoreState.Resolve(k_Passthrough, new DemoRequirements { Passthrough = Need.Off });
            Assert.AreEqual(new CoreState(true, false, true), state);
        }

        [Test]
        public void CatalogFindsEntriesByIndexAndId()
        {
            var catalog = UnityEngine.ScriptableObject.CreateInstance<DemoCatalog>();
            try
            {
                Assert.IsFalse(catalog.TryGet(0, out _));
                Assert.IsFalse(catalog.TryGet(-1, out _));
                Assert.AreEqual(-1, catalog.IndexOf("sandbox"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(catalog);
            }
        }
    }
}
