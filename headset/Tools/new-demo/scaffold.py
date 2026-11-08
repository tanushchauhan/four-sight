#!/usr/bin/env python3
"""Makes a new CardboardXR demo's folder from the templates beside this script.

Run it from the repository's root:

  python3 Tools/new-demo/scaffold.py --name Garden --id garden --title "Garden" \\
      --description "Grow a garden on your table." --tagline "A garden on your table" \\
      --icon palette --tint 0.30,0.60,0.35 --sixdof On --passthrough On --hands On --scan-surfaces

It writes Assets/_Project/Demos/<Name>/ (Scripts, Editor and Tests) and never touches a folder that
already exists. The build, or DemoScenes.MakeMissing, then adds the catalog entry and makes the scene.
"""
import argparse
import pathlib
import re
import sys

ROOT = pathlib.Path.cwd()
DEMOS = ROOT / "Assets/_Project/Demos"
CATALOG = DEMOS / "DemoCatalog.asset"
ICONS = ROOT / "Assets/_Project/Scripts/UI/M3Icons.cs"
CODEPOINTS = ROOT / "Assets/_Project/UI/Fonts/MaterialIconsRound-Regular.codepoints"
TEMPLATES = pathlib.Path(__file__).resolve().parent / "templates"
# Folder names that would clash with the lobby, a folder Unity treats specially, or a type in
# CardboardXR.Demos (the demo's namespace is CardboardXR.Demos.<Name>).
RESERVED = {"Lobby", "Editor", "Scripts", "Tests", "Resources", "Demo", "Demos", "Core", "CoreServices",
            "DemoManager", "DemoCatalog", "AppLibrary", "WorldUi"}


def fail(message):
    print(f"scaffold: {message}", file=sys.stderr)
    sys.exit(1)


def cs_string(text):
    return text.replace("\\", "\\\\").replace('"', '\\"').replace("\n", " ")


def taken_ids():
    ids = set()
    if CATALOG.exists():
        ids.update(re.findall(r"^\s*- Id: (\S+)\s*$", CATALOG.read_text(), re.M))
    for setup in DEMOS.glob("*/Editor/*Setup.cs"):
        ids.update(re.findall(r'\bId\s*=\s*"([^"]+)"', setup.read_text()))
    return ids


def check_icon(icon):
    """None if the app has the icon, else the line to add to M3Icons.cs."""
    if re.search(r'\["%s"\]\s*=' % re.escape(icon), ICONS.read_text()):
        return None
    for line in CODEPOINTS.read_text().splitlines():
        parts = line.split()
        if len(parts) == 2 and parts[0] == icon:
            return f'            ["{icon}"] = 0x{parts[1]},'
    fail(f'"{icon}" isn\'t a Material Icons Round name (see {CODEPOINTS.relative_to(ROOT)})')


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--name", required=True, help="folder, scene and log tag: a capitalized word, such as Garden")
    parser.add_argument("--id", required=True, help="short and lowercase, for logs: garden")
    parser.add_argument("--title", required=True, help="the app library's name for it")
    parser.add_argument("--description", required=True, help="a sentence or two on what you do in it")
    parser.add_argument("--tagline", required=True, help="one line, shown while the icon is looked at")
    parser.add_argument("--icon", default="apps", help="a Material Icons Round name, preferably one in M3Icons.cs")
    parser.add_argument("--tint", default="0.29,0.36,0.54", help="the icon tile's color as r,g,b from 0 to 1")
    needs = ["On", "Off", "Any"]
    parser.add_argument("--sixdof", choices=needs, default="Any")
    parser.add_argument("--passthrough", choices=needs, default="Any")
    parser.add_argument("--hands", choices=needs, default="Any")
    parser.add_argument("--scan-surfaces", action="store_true", help="scan the whole room for tables and walls on entering")
    parser.add_argument("--keep-placed-objects", action="store_true", help="leave placed objects when the demo closes")
    args = parser.parse_args()

    if not DEMOS.is_dir() or not ICONS.exists():
        fail("run this from the CardboardXR repository's root")
    if not re.fullmatch(r"[A-Z][A-Za-z0-9]*", args.name):
        fail(f'--name "{args.name}" should be a capitalized word of letters and digits, such as Garden')
    if args.name in RESERVED:
        fail(f'--name "{args.name}" is taken by the project; pick another')
    if not re.fullmatch(r"[a-z][a-z0-9-]*", args.id):
        fail(f'--id "{args.id}" should be short and lowercase, such as garden')
    folder = DEMOS / args.name
    if folder.exists():
        fail(f"{folder.relative_to(ROOT)} already exists")
    if any(path.name.lower() == args.name.lower() for path in DEMOS.iterdir()):
        fail(f'a demo folder named like "{args.name}" already exists')
    if args.id in taken_ids():
        fail(f'another demo already has the Id "{args.id}"')
    if args.sixdof == "Off" and (args.passthrough == "On" or args.hands == "On"):
        fail("passthrough and hands need 6DoF: with --sixdof Off they can't be On")
    try:
        tint = [float(value) for value in args.tint.split(",")]
    except ValueError:
        tint = []
    if len(tint) != 3 or not all(0.0 <= value <= 1.0 for value in tint):
        fail(f'--tint "{args.tint}" should be three numbers from 0 to 1, such as 0.30,0.60,0.35')
    icon_line = check_icon(args.icon)

    requirements = [f"SixDof = Need.{args.sixdof}", f"Passthrough = Need.{args.passthrough}", f"Hands = Need.{args.hands}"]
    if args.scan_surfaces:
        requirements.append("ScanSurfaces = true")
    if args.keep_placed_objects:
        requirements.append("KeepPlacedObjects = true")
    replacements = {
        "__NAME__": args.name,
        "__ID__": args.id,
        "__TITLE__": cs_string(args.title),
        "__DESCRIPTION__": cs_string(args.description),
        "__TAGLINE__": cs_string(args.tagline),
        "__ICON__": args.icon,
        "__TINT__": ", ".join(f"{value:.2f}f" for value in tint),
        "__REQUIREMENTS__": "new DemoRequirements { " + ", ".join(requirements) + " }",
    }

    written = []
    for template in sorted(TEMPLATES.rglob("*")):
        if not template.is_file():
            continue
        relative = str(template.relative_to(TEMPLATES)).replace("__NAME__", args.name)
        text = template.read_text()
        for key, value in replacements.items():
            text = text.replace(key, value)
        target = folder / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text)
        written.append(target.relative_to(ROOT))

    index = len(re.findall(r"^\s*- Id: ", CATALOG.read_text(), re.M)) if CATALOG.exists() else 0
    print(f"Made {folder.relative_to(ROOT)}:")
    for path in written:
        print(f"  {path}")
    print(f"It will be demo {index} in the catalog: start in it with --ei demo {index}.")
    if icon_line:
        print(f'The app doesn\'t have the icon "{args.icon}" yet. Add this line to Codepoints in '
              f"{ICONS.relative_to(ROOT)}, or pick one that's there:\n{icon_line}")


if __name__ == "__main__":
    main()
