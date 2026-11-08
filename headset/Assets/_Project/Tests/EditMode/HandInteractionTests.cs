using CardboardXR.Hands;
using NUnit.Framework;
using UnityEngine;

namespace CardboardXR.Stereo.Tests
{
    public class HandInteractionTests
    {
        // A ray turned further right the larger x is.
        static Ray RayAt(float x) => new(Vector3.zero, new Vector3(x, 0f, 1f));

        [Test]
        public void AimHistory_GivesTheRayFromBeforeATime()
        {
            var history = new AimHistory(0.5f);
            for (var i = 0; i <= 4; i++)
                history.Add(i * 0.1f, RayAt(i));
            Assert.That(history.TryGetAt(0.25f, out var ray), Is.True);
            Assert.That(ray.direction, Is.EqualTo(RayAt(2).direction));
            Assert.That(history.TryGetAt(1f, out ray), Is.True);
            Assert.That(ray.direction, Is.EqualTo(RayAt(4).direction));
        }

        [Test]
        public void AimHistory_GivesTheOldestWhenAllAreNewer()
        {
            var history = new AimHistory(0.5f);
            history.Add(1f, RayAt(1));
            history.Add(1.1f, RayAt(2));
            Assert.That(history.TryGetAt(0.5f, out var ray), Is.True);
            Assert.That(ray.direction, Is.EqualTo(RayAt(1).direction));
        }

        [Test]
        public void AimHistory_DropsRaysOlderThanItKeeps()
        {
            var history = new AimHistory(0.5f);
            history.Add(0f, RayAt(0));
            history.Add(0.3f, RayAt(1));
            history.Add(0.6f, RayAt(2));
            Assert.That(history.Count, Is.EqualTo(2));
            Assert.That(history.TryGetAt(0.1f, out var ray), Is.True);
            Assert.That(ray.direction, Is.EqualTo(RayAt(1).direction));
        }

        [Test]
        public void AimHistory_EmptyHasNone()
        {
            var history = new AimHistory(0.5f);
            Assert.That(history.TryGetAt(1f, out _), Is.False);
            history.Add(0f, RayAt(0));
            history.Clear();
            Assert.That(history.TryGetAt(1f, out _), Is.False);
        }
    }
}
