using CardboardXR.Demos;
using CardboardXR.Demos.Paint;
using UnityEditor;
using UnityEngine;

namespace ProjectBootstrap
{
    // Air Paint: the stroke materials, a dark floor grid for VR, and the demo.
    class PaintSetup : DemoSetup
    {
        public override string Name => "Paint";
        public override int Order => 30;

        public override DemoCatalog.Entry Entry => new()
        {
            Id = "paint",
            Title = "Air Paint",
            Description = "Paint in the air around you with your hand, like Tilt Brush. Pinch to paint; look at the tool panel and pinch to change color, brush and size.",
            Tagline = "Paint in the air with your hand",
            Icon = "brush",
            Tint = new Color(0.48f, 0.31f, 0.75f),
            Requirements = new DemoRequirements { SixDof = Need.On, Passthrough = Need.On, Hands = Need.On },
        };

        public override void BuildScene(string folder)
        {
            var unlit = DemoAssets.Unlit;
            // White and double-sided: each stroke is colored with a property block, and a ribbon is
            // seen from both sides.
            var solid = StereoBuilder.CreateMaterial("Stroke", unlit, Color.white, null, 1f, folder);
            var glow = StereoBuilder.CreateMaterial("Stroke Glow", unlit, Color.white, null, 1f, folder);
            DemoAssets.MakeAdditive(glow);

            var demo = new GameObject("Paint Demo").AddComponent<PaintDemo>();
            var serializedDemo = new SerializedObject(demo);
            serializedDemo.FindProperty("m_SolidMaterial").objectReferenceValue = solid;
            serializedDemo.FindProperty("m_GlowMaterial").objectReferenceValue = glow;
            serializedDemo.ApplyModifiedPropertiesWithoutUndo();

            // A dark floor with a faint grid every half meter, so there's a sense of space and scale
            // in VR. Passthrough hides it.
            var environment = DemoAssets.Environment();
            var grid = StereoBuilder.SaveAsset(DemoAssets.CreateGridTexture(), $"{folder}/Grid.asset");
            const float floorSize = 30f;
            DemoAssets.Floor(environment.transform,
                StereoBuilder.CreateMaterial("Paint Floor", unlit, Color.white, grid, floorSize / 0.5f, folder), floorSize);
        }
    }
}
