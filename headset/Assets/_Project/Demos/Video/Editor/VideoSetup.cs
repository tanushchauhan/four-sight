using CardboardXR.Demos;
using CardboardXR.Demos.Video;
using UnityEditor;
using UnityEngine;

namespace ProjectBootstrap
{
    // 360 Video: a sphere to draw on, the player and its panel (built at runtime).
    class VideoSetup : DemoSetup
    {
        public override string Name => "Video";
        public override int Order => 20;

        public override DemoCatalog.Entry Entry => new()
        {
            Id = "video",
            Title = "360 Video",
            Description = "Watch 360 and VR180 videos, 2D or 3D, all around you. Tap the video for the controls. Plays videos copied to the phone or linked from Google Drive.",
            Tagline = "360 and VR180 videos all around you",
            Icon = "360",
            Tint = new Color(0.7f, 0.25f, 0.29f),
            Requirements = new DemoRequirements { SixDof = Need.Off, Passthrough = Need.Off, Hands = Need.Off },
        };

        public override void BuildScene(string folder)
        {
            // Double-sided in case the sphere is ever seen from outside, and white so the video's own
            // colors show.
            var material = StereoBuilder.CreateMaterial("Video Sphere", DemoAssets.Unlit, Color.white, null, 1f, folder);

            var sphere = new GameObject("Video Sphere").AddComponent<VideoSphere>();
            var serializedSphere = new SerializedObject(sphere);
            serializedSphere.FindProperty("m_Material").objectReferenceValue = material;
            serializedSphere.ApplyModifiedPropertiesWithoutUndo();

            var demo = new GameObject("Video Demo").AddComponent<VideoDemo>();
            var serializedDemo = new SerializedObject(demo);
            serializedDemo.FindProperty("m_Sphere").objectReferenceValue = sphere;
            serializedDemo.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
