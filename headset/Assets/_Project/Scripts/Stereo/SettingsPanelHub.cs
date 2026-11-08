using System;

namespace CardboardXR.Stereo
{
    // Lets code that can't reach the hands assembly open the app's settings panel: the phone-screen
    // menu, for when there's no hand to summon it with. SettingsSummoner answers it.
    public static class SettingsPanelHub
    {
        public static event Action OpenRequested;

        public static void Open() => OpenRequested?.Invoke();
    }
}
