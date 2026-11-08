using CardboardXR.Demos;
using UnityEngine;

namespace ProjectBootstrap
{
    // Cube Sandbox: cubes on real surfaces, in passthrough. It's Core's own placement and grabbing,
    // so the demo is Demo itself with nothing of its own, and a checker floor in case passthrough is
    // turned off.
    class SandboxSetup : DemoSetup
    {
        public override string Name => "Sandbox";
        public override int Order => 10;

        public override DemoCatalog.Entry Entry => new()
        {
            Id = "sandbox",
            Title = "Cube Sandbox",
            Description = "Your own room, through the camera. Look at a table or the floor and tap or pinch to put a cube there, then pick cubes up with your hand and move them.",
            Tagline = "Put cubes on your real tables and floor, and move them by hand",
            Icon = "view_in_ar",
            Tint = new Color(0.78f, 0.49f, 0.12f),
            Requirements = new DemoRequirements { SixDof = Need.On, Passthrough = Need.On, Hands = Need.On, ScanSurfaces = true },
        };

        public override void BuildScene(string folder)
        {
            new GameObject("Sandbox Demo").AddComponent<Demo>();
            var environment = DemoAssets.Environment();
            var checker = DemoAssets.CreateCheckerTexture(folder);
            var material = StereoBuilder.CreateMaterial("Sandbox Floor", DemoAssets.Unlit, new Color(0.55f, 0.75f, 1f), checker,
                DemoAssets.RoomSize / 2f, folder);
            DemoAssets.Floor(environment.transform, material, DemoAssets.RoomSize, keepCollider: true);
        }
    }
}
