using UnityEngine;

namespace CardboardXR.Stereo
{
    // Keeps the screen bright while the app is in front: a viewer's lenses dim the picture, and the
    // phone's own brightness is set for reading at arm's length. 70% by default rather than full, to
    // spare heat while the visor is on a face; the "brightness" launch option sets another percentage
    // (0 leaves it alone).
    //   On Android this is the app window's brightness, not the system setting, and lapses when the app
    //   leaves the front. On iOS it's the screen's, which iOS can keep after the app leaves, so the
    //   user's own brightness is noted on coming to the front and put back on going to the background or
    //   quitting. It's also saved (k_SavedKey), since an app that's killed (by installing a new build,
    //   a crash, or swiping it away) gets no chance to put it back: started again with the screen still
    //   at this app's level, it takes the saved level as the user's. iOS still dims a hot phone
    //   whatever the app asks. (The screen never sleeps while the app runs: StereoRig sets
    //   Screen.sleepTimeout.) Logs [Stereo].
    public class ScreenBrightness : MonoBehaviour
    {
        const int k_DefaultPercent = 70;
        const string k_SavedKey = "UsersBrightness";

        float m_Level;
        float m_UsersLevel = -1f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Create()
        {
            if (!Application.isMobilePlatform)
                return;
            var percent = LaunchOptions.GetInt("brightness", k_DefaultPercent);
            if (percent <= 0)
                return;
            var holder = new GameObject("Screen Brightness");
            DontDestroyOnLoad(holder);
            holder.AddComponent<ScreenBrightness>().m_Level = Mathf.Clamp01(percent / 100f);
        }

        void Start()
        {
            if (m_UsersLevel < 0f)
                Brighten();
        }

        // iOS also reports an unpause at launch, so only a return from the background brightens again.
        void OnApplicationPause(bool paused)
        {
            if (paused)
                Restore();
            else if (m_UsersLevel < 0f)
                Brighten();
        }

        void OnApplicationQuit() => Restore();

        void Brighten()
        {
            if (m_UsersLevel < 0f)
            {
                var now = Screen.brightness;
                // Still at this app's level: a run that was killed left it there; the user's is the saved one.
                var leftByUs = Mathf.Abs(now - m_Level) < 0.01f && PlayerPrefs.HasKey(k_SavedKey);
                m_UsersLevel = leftByUs ? PlayerPrefs.GetFloat(k_SavedKey) : now;
                PlayerPrefs.SetFloat(k_SavedKey, m_UsersLevel);
                PlayerPrefs.Save();
            }
            Screen.brightness = m_Level;
            Debug.Log($"[Stereo] Screen brightness {m_Level * 100f:F0}% (the user's is {m_UsersLevel * 100f:F0}%)");
        }

        void Restore()
        {
            if (m_UsersLevel < 0f)
                return;
            Screen.brightness = m_UsersLevel;
            m_UsersLevel = -1f;
        }
    }
}
