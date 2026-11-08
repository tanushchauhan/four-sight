---
name: new-demo
description: Turn a description of a new CardboardXR demo (an experience in the app's library) into a working demo built on Core's shared head tracking, AR, hand tracking, gaze-and-pinch input and Material UI. Use when someone wants to add, make, prototype or scaffold a new demo, app, game or experience for CardboardXR, or describes an idea for one.
---

# New CardboardXR demo

A demo is one folder, `Assets/_Project/Demos/<Name>/`, that reaches Core through `CoreServices` and
joins the app through its `DemoSetup`. Your job is to turn the person's idea into that folder, keep
the idea within the project's design rules, and leave it compiling with its tests passing.

Work from the root of a CardboardXR clone. Every path below is relative to it, wherever this file
is installed. The script and templates are always used from the clone's `Tools/new-demo`, so they
match the code.

## 1. Read before planning

- Read all of `ADDING_A_DEMO.md`. It's the contract: the folder, what Core provides, the pinch
  order, the requirements and the design rules.
- Read the existing demo closest to the idea, setup and scripts both:
  - **Portal** places a prefab on the floor and has a panel. It's the smallest one.
  - **Paint** takes pinches (`IPinchHandler`) and follows the hand.
  - **Rush** is a game with rounds, a score, and a pause while the settings panel is open.
  - **Video** is 3DoF content with no hands.
  - **Sandbox** is only Core's own placing and grabbing.

## 2. Pin the idea down

Work out these answers from the description. Ask only about what you can't infer, in one round of at
most four questions:

- **What the person does**, moment to moment, and what in it is new compared with the five demos
  above.
- **Where it happens:**
  - in their real room (passthrough);
  - in a virtual world;
  - or a doorway from one into the other.
- **The hand:** does it need the hand's position (hitting, drawing, reaching), or only clicks?
  Gaze and pinch clicks work without hand tracking.
- **Real surfaces:** does it put things on the floor (`Placer`), or on tables and walls as well
  (`ScanSurfaces`)?
- **Moving:** does the person walk around (6DoF), or sit and look around (3DoF is enough)?

Then map the answers to requirements:

| The idea | SixDof | Passthrough | Hands | Other |
|---|---|---|---|---|
| Something to look around at, sitting | `Off` or `Any` | `Off` | `Off` | |
| A virtual world to walk around | `On` | `Off` | `Any` | |
| Virtual things in the real room | `On` | `On` | `Any` | `ScanSurfaces` for tables and walls |
| Uses the hand's position | `On` | either | `On` | |

Use `Any` wherever the demo doesn't care, and `Off` where it's better without. Hand tracking costs
the most heat.

## 3. Check it against the design rules

Go through each rule under "Design rules" in `ADDING_A_DEMO.md`. If the idea breaks one, say so
plainly and suggest a version that doesn't. Don't quietly build around it. The usual problems:

- moving the view without the head moving;
- dropping or undoing something when the hand goes out of view;
- grabbing with the gaze;
- needing the network;
- an idea that comes down to carrying objects around a table, which is the sandbox again.

## 4. Agree on a short spec

Write one short block and get the person's OK before writing code. It covers:

- Title, Name (folder), Id, tagline, icon, tint and requirements.
- What the person does, and what the panel shows.
- How pinches are used, and what happens when the hand is lost.
- What gets logged, and which pure logic gets tests.

For the icon, prefer a name already in `Assets/_Project/Scripts/UI/M3Icons.cs`. A new icon means one
added line in that shared file.

## 5. Scaffold

From the repository's root:

```sh
python3 Tools/new-demo/scaffold.py --name Garden --id garden --title "Garden" \
    --description "..." --tagline "..." --icon palette --tint 0.30,0.60,0.35 \
    --sixdof On --passthrough On --hands Any --scan-surfaces
```

It checks the names, the requirements and the icon, and refuses to overwrite anything. It writes
`Scripts/` (the asmdef and `<Name>Demo.cs`), `Editor/<Name>Setup.cs` and `Tests/` (the asmdef only).
It also prints the demo's index for the `demo` launch option and, if the icon is missing, the exact
line to add to `M3Icons.cs`.

## 6. Build the demo

- **Stay inside the demo's folder.** The only exceptions are `DemoCatalog.asset`, which the build
  writes, and a new icon line in `M3Icons.cs`. If the idea seems to need a change in Core, stop and
  ask, because a Core change affects every demo.
- **Reach Core only through the `CoreServices` passed to `OnDemoEnter`.**
- **Panels:** build them at runtime with `WorldUi.Panel` and `M3.Text`, `M3.Button`,
  `M3.IconButton` and `M3.Slider`.
- **Pinches:** take them with `PinchRouter.Handler` only if the demo uses them for more than
  buttons. Otherwise remove `IPinchHandler` from the template.
- **The starter scene:** put what it needs (materials, prefabs, textures made in code) in
  `<Name>Setup.BuildScene`, saved in the demo's folder. The scene is made once and never
  overwritten. If you change `BuildScene` after the scene exists, and the scene hasn't been edited by
  hand, delete `<Name>.unity` so it's made again.
- **Clean up in `OnDemoExit`:** destroy anything parented into Core (such as under
  `core.Boundary.transform`) and unsubscribe from Core events.
- **Logs:** start every log line with `[<Name>]`. Log when the demo is ready, what the person
  does, and results such as a score or a count.
- **Tests:** put rules and math in static, pure functions, as `RushRules` and
  `PortalWorld.Crossing` do, and test them in `Tests/` (see `RushRulesTests`). Use the namespace
  `CardboardXR.Demos.<Name>.Tests`.
- **Assets:** no network, and no assets from outside the repository. Make them in code, or add
  files whose license allows it.

## 7. Check it

Unity's batch mode needs the project closed in the Editor. Check with
`pgrep -f Unity.app/Contents/MacOS/Unity`. If the Editor is open, ask the person to close it, or to
use **CardboardXR > Make Missing Demo Scenes** and the Test Runner instead.

```sh
U=${UNITY:-/Applications/Unity/Hub/Editor/$(sed -n 's/^m_EditorVersion: //p' ProjectSettings/ProjectVersion.txt)/Unity.app/Contents/MacOS/Unity}
"$U" -batchmode -quit -projectPath . -executeMethod ProjectBootstrap.DemoScenes.MakeMissing -logFile make.log
"$U" -batchmode -projectPath . -runTests -testPlatform EditMode -testResults results.xml -logFile tests.log
```

- In `make.log`, look for `[DemoScenes] Added <id> to the catalog`, `Made .../<Name>.unity`, and no
  `error CS` or `[DemoScenes]` errors.
- In `results.xml`, every test should pass, including the demo's own.
- In `git status`, only the demo's folder (with the `.meta` files Unity made), `DemoCatalog.asset`
  and possibly `M3Icons.cs` should have changed.

Don't build for a phone or install unless the person asks. Building and installing are theirs to
start, with `zsh Tools/build_and_run.sh --ei demo <index>` (Android) or
`zsh Tools/ios_build_and_run.sh --ei demo <index>` (iOS).

## 8. Hand it over

Tell the person:

- what the demo does;
- which files it has;
- the command to start straight in it.

Give them a test checklist for this demo in particular: the general checks under "Testing" in
`ADDING_A_DEMO.md`, plus what's specific to it (for example "hit an orb with your hand half out of
view"). Don't commit unless they ask.

The demo stays in the person's own build. The CardboardXR repository doesn't take new demos, so
don't suggest a pull request upstream. If they plan to install it next to the released app or share
it, point them to "Your own build" in `ADDING_A_DEMO.md`.
