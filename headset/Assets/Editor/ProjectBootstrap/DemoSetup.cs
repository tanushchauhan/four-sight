using CardboardXR.Demos;

namespace ProjectBootstrap
{
    // One demo's entry in the app library and how to make its starter scene. Each demo has one in its
    // own folder, at Assets/_Project/Demos/<Name>/Editor/<Name>Setup.cs, so adding a demo touches
    // nothing outside that folder: DemoScenes finds every subclass, adds its entry to the catalog when
    // the catalog doesn't have it yet, and makes its scene when the scene is missing. Neither is ever
    // overwritten, so both can be edited by hand afterwards. That Editor folder has no asmdef: it
    // compiles with the project's other editor code, so it can use DemoAssets and StereoBuilder's
    // helpers.
    public abstract class DemoSetup
    {
        // The demo's folder under Assets/_Project/Demos and its scene's name: "Portal" for
        // Portal/Portal.unity. Also its log tag: lines that start with [Portal] go to the session log.
        public abstract string Name { get; }

        // The app library entry: Id, Title, Description, Tagline, Icon, Tint and Requirements.
        // ScenePath is filled in from Name.
        public abstract DemoCatalog.Entry Entry { get; }

        // Where the entry goes in the app library when it's first added, smallest first (this
        // repository's demos use 10 to 50). After that the catalog's own order is kept.
        public virtual int Order => 100;

        // Fills a new, empty, open scene: the demo's root (a Demo subclass) and what it needs, with
        // any assets it makes saved in folder. DemoScenes saves the scene.
        public abstract void BuildScene(string folder);

        public string Folder => $"{DemoScenes.DemosFolder}/{Name}";
        public string ScenePath => $"{Folder}/{Name}.unity";
    }
}
