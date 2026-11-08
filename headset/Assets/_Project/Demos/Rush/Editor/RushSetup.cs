using CardboardXR.Demos;
using CardboardXR.Demos.Rush;
using UnityEditor;
using UnityEngine;

namespace ProjectBootstrap
{
    // Orb Rush: the orbs' core and glowing halo, the track's rails, a dark floor grid, and the demo.
    class RushSetup : DemoSetup
    {
        public override string Name => "Rush";
        public override int Order => 40;

        public override DemoCatalog.Entry Entry => new()
        {
            Id = "rush",
            Title = "Orb Rush",
            Description = "Orbs fly toward you: hit them with your hand as they reach the rings. One hand, a minute a round, getting faster.",
            Tagline = "Hit the incoming orbs with your hand",
            Icon = "bolt",
            Tint = new Color(0.12f, 0.55f, 0.62f),
            Requirements = new DemoRequirements { SixDof = Need.On, Passthrough = Need.Off, Hands = Need.On },
        };

        public override void BuildScene(string folder)
        {
            var unlit = DemoAssets.Unlit;
            // White: each orb is colored with a property block.
            var orb = StereoBuilder.CreateMaterial("Orb", unlit, Color.white, null, 1f, folder);
            var halo = StereoBuilder.CreateMaterial("Orb Halo", unlit, Color.white, null, 1f, folder);
            DemoAssets.MakeAdditive(halo);
            var rail = StereoBuilder.CreateMaterial("Rail", unlit, new Color(0.25f, 0.4f, 0.7f), null, 1f, folder);

            var demo = new GameObject("Rush Demo").AddComponent<RushDemo>();
            var serializedDemo = new SerializedObject(demo);
            serializedDemo.FindProperty("m_OrbMaterial").objectReferenceValue = orb;
            serializedDemo.FindProperty("m_HaloMaterial").objectReferenceValue = halo;
            serializedDemo.FindProperty("m_RailMaterial").objectReferenceValue = rail;
            serializedDemo.ApplyModifiedPropertiesWithoutUndo();

            var environment = DemoAssets.Environment();
            var grid = StereoBuilder.SaveAsset(DemoAssets.CreateGridTexture(), $"{folder}/Grid.asset");
            const float floorSize = 40f;
            DemoAssets.Floor(environment.transform,
                StereoBuilder.CreateMaterial("Rush Floor", unlit, Color.white, grid, floorSize / 0.5f, folder), floorSize);
        }
    }
}
