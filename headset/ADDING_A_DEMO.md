# Adding a demo

A demo is a scene that loads on top of Core, the scene that keeps the head tracking, AR, hands, gaze
and settings panel running. Core switches on what the demo asks for when it opens and puts things
back when it closes. A demo reaches Core through one object, `CoreServices`, and lives in a single
folder. It doesn't edit any shared file to join the app.

Make one by hand with this guide, or have a coding agent make it with the skill in
[Tools/new-demo](Tools/new-demo). You describe the idea, and the agent:
- turns it into the folder below;
- checks it against the [design rules](#design-rules);
- builds it.

[Using a coding agent](#using-a-coding-agent) explains how to set the skill up.

The demos you make are for your own build. This repository doesn't take new demos into the app (see
[Your own build](#your-own-build)).

## The folder

```
Assets/_Project/Demos/<Name>/
  Scripts/
    CardboardXR.Demos.<Name>.asmdef    the demo's runtime assembly
    <Name>Demo.cs                      subclass of Demo: the scene's root
  Editor/
    <Name>Setup.cs                     subclass of DemoSetup: the app library entry and starter scene
  Tests/
    CardboardXR.Demos.<Name>.Tests.asmdef
    <Name>Tests.cs                     EditMode tests of the demo's pure logic
  <Name>.unity                         made by the setup on the first build, then edited by hand
```

`<Name>` is the folder name, the scene name and the log tag, all the same word: `Portal` means
`Portal/Portal.unity` and log lines that start with `[Portal]`. The `Editor` folder has no asmdef,
which is on purpose: it compiles with the project's editor code (`Assets/Editor/ProjectBootstrap`), so
the setup can use `DemoAssets` and `StereoBuilder`'s helpers.

[Portal](Assets/_Project/Demos/Portal) is the smallest complete example: it places a prefab on the
floor, has a panel and makes its own sound. [Air Paint](Assets/_Project/Demos/Paint) takes pinches
and follows the hand. [Orb Rush](Assets/_Project/Demos/Rush) is a game with rounds and a score.

## How a demo joins the app

On every build, `DemoScenes` finds each `DemoSetup` subclass and:

- **Checks it.** The `Name` has to be a capitalized word, the `Id` short and lowercase, and both
  unique, with a `Title`. If one is wrong, the build stops with a `[DemoScenes]` error that says why.
- **Adds its entry to `DemoCatalog.asset`** if there isn't one with its `Id`. The entry is never
  overwritten, so a title or tint changed in the Inspector stays. `Order` only places a new entry.
  The app library shows the entries in catalog order, six to a page, and adds pages as needed. A
  new demo gets a tile with no UI work.
- **Makes its scene** if `<Name>.unity` is missing, by calling `BuildScene` on a new, empty scene.
  That scene is never overwritten either. Delete it to have it made again.

To do this without building, use **CardboardXR > Make Missing Demo Scenes** in the Editor, or run
this with Unity closed:

```sh
/Applications/Unity/Hub/Editor/<version>/Unity.app/Contents/MacOS/Unity -batchmode -quit \
    -projectPath . -executeMethod ProjectBootstrap.DemoScenes.MakeMissing -logFile -
```

The catalog is the one file two new demos both change. If it conflicts in a merge, take either side
and build: each demo's missing entry is added back from its setup.

**Icons.** The app bundles only the Material Icons (Round) it uses, listed in
[M3Icons.cs](Assets/_Project/Scripts/UI/M3Icons.cs), and a name that isn't listed shows nothing.
Use one that's already there for the app library icon and the demo's buttons if one fits. For a new
one, add a line with its codepoint from
`Assets/_Project/UI/Fonts/MaterialIconsRound-Regular.codepoints`. That's the only shared file a demo
might touch.

Unity makes `.meta` files for the new files the first time it opens them. Commit those too.

## What a demo gets from Core

`Demo.OnDemoEnter(CoreServices core)` runs once the scene has loaded and Core has switched to what
the demo needs. `OnDemoExit(core)` runs before the scene unloads.

| `core.` | What it is | Used by |
|---|---|---|
| `Head` | The head's transform: where the user is and which way they look | every demo |
| `Placer` | Puts `ObjectPrefab` where the gaze meets a surface on a pinch; its `Placed` event gives you the object, `RemoveAll()` clears them | Portal, Sandbox |
| `HandTracker` | `IsTracking`, `Joints[HandGeometry.IndexTip]` (21 joints, world space), `PinchPoint`, and `IsPaused` while the room scan runs | Paint, Rush |
| `Boundary` | The room: its transform is the room's space, and it knows the floor and scanned surfaces | Paint, Rush |
| `Settings` | The settings panel, with an `OpenChanged` event (Rush pauses a round while it's open) | Rush |
| `Passthrough` | The camera view of the room, if a demo needs to switch it itself | Paint |
| `Gaze` | The gaze pointer: `Target` is what the head is looking at | |
| `Demos` | `ExitToLobby()`, for a demo's own way out | |
| `State`, `Apply()` | The 6DoF, passthrough and hands switches, which `DemoRequirements` normally sets | |

**What the demo needs** goes in its entry's `Requirements`, and DemoManager applies it:

| Field | Values | Notes |
|---|---|---|
| `SixDof` | `On`, `Off`, `Any` | Walking around, surfaces and anchors need 6DoF. 3DoF only turns with the head |
| `Passthrough` | `On`, `Off`, `Any` | The camera's view of the room. `On` turns 6DoF on |
| `Hands` | `On`, `Off`, `Any` | Full hand tracking. `On` turns 6DoF on. It's the biggest share of the phone's heat, so only ask for it if the demo uses the hand's position |
| `ScanSurfaces` | `true`/`false` | Scan the whole room for tables and walls on entering, not just the floor |
| `KeepPlacedObjects` | `true`/`false` | Leave placed objects when the demo closes |

`SixDof = Off` turns passthrough off too. The 360 video demo runs in 3DoF with no hands, and pinch
still works there through a lighter camera detector.

**Cleaning up.** While a demo runs, its scene is the active one, so whatever it creates is unloaded
with it. Two things aren't:

- Anything parented into Core. Paint and Rush put their content under `core.Boundary.transform` so it
  stays put in the room, and destroy it in `OnDemoExit`.
- Events the demo subscribed to on Core objects. Unsubscribe in `OnDemoExit`.

DemoManager handles the rest when the demo closes: placed objects, the placer's prefab, the pinch
handler, and the 6DoF, passthrough and hands state.

## Input

Input is **gaze and pinch** everywhere: look at something and pinch in front of the camera (or tap
the screen) to click it. Every pinch goes through one order, in `PinchRouter`:

1. The palm's settings gear, if it's showing.
2. A button under the gaze, so a demo's panels always work.
3. **The demo**, if it set `PinchRouter.Handler` (an `IPinchHandler`) in `OnDemoEnter` and the
   settings panel isn't open. `TryBeginPinch(gazeTarget, source)` returns true to take the pinch,
   and `EndPinch()` is called when the fingers open or the hand is lost. A `PinchSource.Camera`
   pinch (3DoF) has no hand position, so a handler that needs one turns it down.
4. Picking up whatever the hand's ray is on (6DoF). Objects with an `XRGrabInteractable` and
   `Rigidbody` are picked up this way; add `CubePhysics` for the sandbox cubes' behavior.
5. Otherwise, a click on whatever the gaze is on.

A click within 0.35 s of the last click is ignored, since a pinch is sometimes seen twice.

**Panels** are built at runtime with the Material kit: `WorldUi.Panel(...)` makes a panel the gaze
can use, `M3.Text`, `M3.Button`, `M3.IconButton` and `M3.Slider` fill it, and
`WorldUi.PlaceInFront(panel, core.Head, distance, pitch)` places it. Sizes are in dp. The lobby
button is on the settings panel, so a demo doesn't need its own.

## Design rules

These come from building and testing the demos so far.

- **Make it feel different from what's here.** The demos that work each do one thing you can't do
  elsewhere: paint in the air, walk through a door, hit things with your hand. A block stacker was
  cut because carrying objects around a table felt like the cube sandbox again. Before building,
  say what the user does that's new.
- **Don't move the view.** The view moves only when the user moves. Turning the head is how you look
  around, and walking (in 6DoF) is how you get somewhere. Dragging the 360 video around with a pinch
  was tried and dropped for comfort: a view that moves while the head doesn't is what makes people
  feel sick in VR.
- **Losing the hand isn't letting go.** Tracking drops out at the edges of the camera's view,
  often. A held object freezes where it is, and a stroke ends cleanly. Don't drop, throw or undo
  anything because the hand went missing for a moment.
- **Grab with the hand, click with the gaze.** Picking things up uses the hand's ray. Gaze-and-pinch
  is for buttons and surfaces. Don't add gaze grabbing.
- **Ask Core for as little as possible.** Hands and passthrough cost heat and battery. Use `Any`
  when the demo doesn't care, and `Off` when it's better without.
- **Work offline.** The app has no network access apart from two features the user starts. Bundle
  assets in the demo's folder, or make them in code like Portal's sky and its sound.
- **Keep the frame rate.** The visor build targets 60 fps. Watch the `[Frames]` and
  `[Hitch]` log lines while testing, and avoid allocating every frame.
- **Log what happened.** Start log lines with `[<Name>]`: when the demo is ready, what the user did,
  and results such as a score or a count. These lines go to the phone's session log, and they're
  how a test run gets read back afterwards.

## Testing

Put the logic that doesn't need a device in static, pure functions, as `RushRules`,
`StrokeMesh.Build` and `PortalWorld.Crossing` do, and test them in the demo's `Tests` folder. The
Android build script runs every EditMode test before it builds.

Then on a phone, start straight in the demo with its index in the catalog, from 0 (the demos
already here are 0 to 4):

```sh
zsh Tools/build_and_run.sh --ei demo 5        # Android
zsh Tools/ios_build_and_run.sh --ei demo 5    # iOS
```

Check:

- It opens from the app library and from the launch option, and the lobby comes back as it was
  when you leave.
- Every control works with a pinch and with a screen tap.
- Hands going out of view and coming back doesn't break anything.
- The settings panel opens over it, and the demo behaves while it's open.
- The log has the demo's `[<Name>]` lines and no exceptions, and `[Frames]` stays near the target.
- Test on one phone for most changes. Test on both Android and iOS if the demo touches the camera,
  AR, video decoding, native code or the frame rate.

## Your own build

Your demos stay in your own build. Pull requests that add demos to this repository won't be merged.
Keep your demos in a fork or your own clone, and build and install it as
[the README](README.md#build-and-run) describes.

- **Use your own app id.** Set `CARDBOARDXR_BUNDLE_ID` to an id of your own, such as
  `com.example.cardboardxr`. Then your build installs next to this project's released app instead of
  replacing it. On iOS you need your own id anyway, since an id belongs to the Apple team that first
  signed it.
- **Updating from this repository.** Your demos are in their own folders, so pulling new changes
  rarely conflicts. `DemoCatalog.asset` is the one file that can. Take this repository's version and
  build, and your demos' entries are added back from their setups. Any changes you made to those
  entries by hand need making again.
- **Sharing your build.** You can share your fork or your builds under the terms of the Apache 2.0
  [license](LICENSE): include the license and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md), and
  say what you changed.

## Using a coding agent

[Tools/new-demo](Tools/new-demo) holds a skill for coding agents. The project doesn't install it
into any agent; you set it up in yours. The folder has:

- **`SKILL.md`**: the steps the agent follows, from questions about the idea to a checked build. It
  uses the Agent Skills format, a folder with a `SKILL.md` whose header names the skill and says
  when to use it.
- **`scaffold.py`**: makes a demo's folder from `templates/`. It needs only Python 3, and you can
  run it yourself: `python3 Tools/new-demo/scaffold.py --help`.

How you set it up depends on the agent:

- **An agent that loads skills from a folder:** link `Tools/new-demo` into the folder it loads
  skills from, so the skill stays up to date with your clone. For Claude Code, that folder is
  `~/.claude/skills/`. From the repository's root:

  ```sh
  mkdir -p ~/.claude/skills && ln -s "$PWD/Tools/new-demo" ~/.claude/skills/new-demo
  ```

  Other agents keep skills in their own folder, so see their documentation. Once it's linked, ask
  for a new demo, or start the skill by name (`/new-demo` in Claude Code).
- **Any other agent:** name the file in your request, for example "Follow
  Tools/new-demo/SKILL.md to make a demo where you grow a garden on your table". Or add a line
  like that to the instructions file your agent reads.

Wherever it's installed, the skill works only inside a clone of this repository. It runs
`scaffold.py` and reads the templates from the clone's `Tools/new-demo`.
