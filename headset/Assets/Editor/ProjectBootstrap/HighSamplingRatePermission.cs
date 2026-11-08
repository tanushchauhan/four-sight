using System.IO;
using System.Xml;
using UnityEditor.Android;

namespace ProjectBootstrap
{
    // Declares HIGH_SAMPLING_RATE_SENSORS, without which Android 12+ caps the head tracker's native
    // sensor reader (CardboardXRImu.cpp) at 200 Hz. It's a normal permission, granted at install
    // without a prompt.
    class HighSamplingRatePermission : IPostGenerateGradleAndroidProject
    {
        const string k_AndroidNamespace = "http://schemas.android.com/apk/res/android";
        const string k_Permission = "android.permission.HIGH_SAMPLING_RATE_SENSORS";

        public int callbackOrder => 0;

        // path is the generated unityLibrary module.
        public void OnPostGenerateGradleAndroidProject(string path)
        {
            var manifestPath = Path.Combine(path, "src", "main", "AndroidManifest.xml");
            var document = new XmlDocument();
            document.Load(manifestPath);
            var manifest = document.DocumentElement;
            foreach (XmlElement permission in manifest.GetElementsByTagName("uses-permission"))
            {
                if (permission.GetAttribute("name", k_AndroidNamespace) == k_Permission)
                    return;
            }

            var element = document.CreateElement("uses-permission");
            element.SetAttribute("name", k_AndroidNamespace, k_Permission);
            manifest.PrependChild(element);
            document.Save(manifestPath);
        }
    }
}
