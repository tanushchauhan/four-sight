using CardboardXR.Demos;
using NUnit.Framework;

namespace CardboardXR.Stereo.Tests
{
    // A demo's log lines reach the session log by its scene's name, with no list of demo tags to keep.
    public class DemoLogTagTests
    {
        [Test]
        public void TagIsTheScenesName()
        {
            Assert.AreEqual("[Portal]", DemoCatalog.Entry.LogTagFor("Assets/_Project/Demos/Portal/Portal.unity"));
            Assert.AreEqual("[Video]", new DemoCatalog.Entry { ScenePath = "Assets/_Project/Demos/Video/Video.unity" }.LogTag);
            Assert.IsNull(new DemoCatalog.Entry().LogTag);
        }

        [Test]
        public void KeptTagsAreKept()
        {
            // A tag no other test or demo uses, since the session log's tags last the whole run.
            const string tag = "[LogTagTestDemo]";
            Assert.IsFalse(SessionLog.IsTagged(tag + " Ready"));
            SessionLog.KeepTag(tag);
            SessionLog.KeepTag(tag);
            Assert.IsTrue(SessionLog.IsTagged(tag + " Ready"));
            Assert.IsFalse(SessionLog.IsTagged("Ready " + tag));
        }

        [Test]
        public void CoreTagsAreKeptAndEmptyOnesIgnored()
        {
            SessionLog.KeepTag(null);
            SessionLog.KeepTag("");
            Assert.IsTrue(SessionLog.IsTagged("[Demos] Entering portal"));
            Assert.IsFalse(SessionLog.IsTagged("Untagged line"));
        }
    }
}
