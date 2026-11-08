using CardboardXR.Demos;
using CardboardXR.Demos.__NAME__;
using UnityEngine;

namespace ProjectBootstrap
{
    // __TITLE__: its app library entry and its starter scene (see DemoSetup). The build adds the
    // entry and makes the scene once, then never overwrites either.
    class __NAME__Setup : DemoSetup
    {
        public override string Name => "__NAME__";

        public override DemoCatalog.Entry Entry => new()
        {
            Id = "__ID__",
            Title = "__TITLE__",
            Description = "__DESCRIPTION__",
            Tagline = "__TAGLINE__",
            Icon = "__ICON__",
            Tint = new Color(__TINT__),
            Requirements = __REQUIREMENTS__,
        };

        public override void BuildScene(string folder)
        {
            new GameObject("__NAME__ Demo").AddComponent<__NAME__Demo>();

            // What the demo shows in VR, which passthrough hides: a dark floor with a faint grid every
            // half meter, for a sense of space and scale.
            var environment = DemoAssets.Environment();
            var grid = StereoBuilder.SaveAsset(DemoAssets.CreateGridTexture(), $"{folder}/Grid.asset");
            const float floorSize = 30f;
            DemoAssets.Floor(environment.transform,
                StereoBuilder.CreateMaterial("__NAME__ Floor", DemoAssets.Unlit, Color.white, grid, floorSize / 0.5f, folder), floorSize);
        }
    }
}
