using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.iOS.Xcode;
using UnityEngine;

namespace ProjectBootstrap
{
    // Info.plist settings Unity has no option for. Lets the app draw faster than 60 fps on a
    // ProMotion iPhone, and shares its Documents folder so videos can be copied in. Without
    // CADisableMinimumFrameDuration, CoreAnimation holds every app to a frame duration of 1/60 s
    // however high Application.targetFrameRate is, and the iPhone 15 Pro's 120 Hz display goes to
    // waste. Unity writes the key as false, so this sets it once the Xcode project exists.
    public class IosProMotion : IPostprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPostprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.iOS)
                return;

            var path = Path.Combine(report.summary.outputPath, "Info.plist");
            if (!File.Exists(path))
            {
                Debug.LogWarning($"[ProjectSetup] No Info.plist at {path}; the display stays at 60 fps.");
                return;
            }

            var plist = new PlistDocument();
            plist.ReadFromFile(path);
            // Both: the second is the one that counts on iPhones, the first on iPads.
            plist.root.SetBoolean("CADisableMinimumFrameDuration", true);
            plist.root.SetBoolean("CADisableMinimumFrameDurationOnPhone", true);
            // The app's Documents folder shows in Finder (the phone's Files tab) and the Files app,
            // so videos can be copied into Documents/Videos for the video demo.
            plist.root.SetBoolean("UIFileSharingEnabled", true);
            plist.root.SetBoolean("LSSupportsOpeningDocumentsInPlace", true);
            plist.WriteToFile(path);
            Debug.Log("[ProjectSetup] ProMotion allowed: the display can run above 60 fps. Documents shared with Finder and Files.");
        }
    }
}
