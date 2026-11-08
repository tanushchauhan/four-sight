using UnityEngine;

namespace CardboardXR.Demos
{
    // The root of a demo scene. DemoManager calls OnDemoEnter once the scene has loaded and Core has
    // switched to what the demo needs, and OnDemoExit before unloading it, while Core is still as
    // the demo left it. Subclass it for each demo; anything a demo changed in Core that DemoManager
    // doesn't put back (see DemoRequirements) is the demo's to put back in OnDemoExit.
    public class Demo : MonoBehaviour
    {
        public virtual void OnDemoEnter(CoreServices core) { }

        public virtual void OnDemoExit(CoreServices core) { }
    }
}
