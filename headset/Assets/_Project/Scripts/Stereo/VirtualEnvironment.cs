using System.Collections.Generic;
using UnityEngine;

namespace CardboardXR.Stereo
{
    // A virtual surrounding, such as a room, that passthrough hides so the camera image shows
    // instead. It can be in any scene, including a demo loaded after passthrough came on, which is
    // why PassthroughController finds these rather than holding a list. Goes on the environment's
    // root, which must be active when its scene loads.
    public class VirtualEnvironment : MonoBehaviour
    {
        static readonly List<VirtualEnvironment> s_All = new();
        static bool s_Hidden;

        // Awake and OnDestroy rather than OnEnable and OnDisable, since hiding deactivates it.
        void Awake()
        {
            s_All.Add(this);
            if (s_Hidden)
                gameObject.SetActive(false);
        }

        void OnDestroy() => s_All.Remove(this);

        public static void SetAllHidden(bool hidden)
        {
            s_Hidden = hidden;
            foreach (var environment in s_All)
                environment.gameObject.SetActive(!hidden);
        }
    }
}
