#!/bin/zsh
# Builds the iPhone version, signs it, installs it and launches it, then prints the session log.
#   zsh Tools/ios_build_and_run.sh            # Unity export, then Xcode, then the phone
#   zsh Tools/ios_build_and_run.sh --xcode    # skip Unity and rebuild from the existing export
#   zsh Tools/ios_build_and_run.sh --ei dof 6 --ei passthrough 1   # with launch options
# Launch options come last, as on Android, and go to Tools/ios_launch.sh, which does the launching.
# The Unity step takes a few minutes and switches the active build target to iOS, so the next
# Android build re-imports assets. --xcode skips it when only native or signing changes matter.
#
# Needs, once, on this Mac: Xcode signed into an Apple ID (Xcode > Settings > Accounts; a free
# personal team is enough), the iPhone connected and trusted, and Developer Mode on the phone
# (Settings > Privacy & Security), which only appears after the first install attempt. Until
# Developer Mode is on, devicectl lists the phone as "connected (no DDI)": it can see it but can't
# run anything on it. The first time, doing that setup through Xcode's own UI is the sure path.
set -u
P=${0:A:h:h}
# Another Unity 6000.6 install: UNITY=/path/to/Unity.app/Contents/MacOS/Unity zsh Tools/...
U=${UNITY:-/Applications/Unity/Hub/Editor/6000.6.8f1/Unity.app/Contents/MacOS/Unity}
# Another app id (an iOS bundle id belongs to the Apple team that first signed it):
#   CARDBOARDXR_BUNDLE_ID=com.example.cardboardxr zsh Tools/...
BUNDLE=${CARDBOARDXR_BUNDLE_ID:-com.brenpoly.cardboardxr}
XCODE_PROJECT=$P/Builds/iOS/Unity-iPhone.xcodeproj
DERIVED=$P/Builds/ios-derived
LOGS=${TMPDIR:-/tmp}/cardboardxr
mkdir -p $LOGS
cd $P || exit 1

XCODE_ONLY=0
if [ "${1:-}" = "--xcode" ]; then XCODE_ONLY=1; shift; fi
# Reject a mistyped option now rather than after a five-minute build.
zsh $P/Tools/ios_launch.sh --check "$@" || exit 1

if [ $XCODE_ONLY = 0 ]; then
    if pgrep -f "Unity.app/Contents/MacOS/Unity" >/dev/null; then echo "Unity is running; close it first"; exit 1; fi
    zsh $P/Tools/make_videos.sh || exit 1
    echo "== Unity export"
    rm -f $LOGS/ios.log
    $U -batchmode -quit -projectPath $P -buildTarget iOS \
        -executeMethod ProjectBootstrap.StereoBuilder.BuildIos -logFile $LOGS/ios.log
    grep -E "\[Build\]|error CS" $LOGS/ios.log | head -10
    grep -q "Build Succeeded" $LOGS/ios.log || { echo "Unity export failed; see $LOGS/ios.log"; exit 1; }
fi

# The phone, by UDID. Xcode remembers paired devices even when they're unplugged ("unavailable"), so
# this takes one that's reachable: "connected", or "available (paired)", which an idle phone drops
# to and devicectl reconnects on first use. Override with CARDBOARDXR_UDID=... to pick a specific
# phone when more than one is plugged in.
UDID=${CARDBOARDXR_UDID:-$(xcrun devicectl list devices --columns UDID \
    --hide-default-columns --hide-headers \
    --filter "State BEGINSWITH 'available' OR State BEGINSWITH 'connected'" 2>/dev/null \
    | head -1 | tr -d '[:space:]')}
if [ -z "$UDID" ]; then
    echo "No connected iPhone. Plug it in, unlock it, and trust this Mac."
    echo "Or set CARDBOARDXR_UDID to the UDID from: xcrun devicectl list devices"
    exit 1
fi
echo "== iPhone $UDID"

# The team the certificate belongs to, so Xcode can sign without being asked. Override with TEAM=...
# Missing isn't fatal: xcodebuild can make a certificate itself from a signed-in Apple ID, and
# Xcode's own UI is the surest way to set it up the first time.
TEAM=${TEAM:-$(security find-certificate -c "Apple Development" -p 2>/dev/null \
    | openssl x509 -noout -subject 2>/dev/null | sed -n 's/.*OU *= *\([A-Z0-9]\{10\}\).*/\1/p')}
if [ -n "$TEAM" ]; then
    echo "== team $TEAM"
    TEAM_SETTING=(DEVELOPMENT_TEAM=$TEAM)
else
    echo "== no certificate yet; letting Xcode try to make one"
    echo "   If this fails: open Builds/iOS/Unity-iPhone.xcodeproj, pick your team under"
    echo "   Signing & Capabilities, and Run once. After that this script works on its own."
    TEAM_SETTING=()
fi

echo "== Xcode build and sign"
xcodebuild -project $XCODE_PROJECT -scheme Unity-iPhone -configuration Debug \
    -destination "id=$UDID" -derivedDataPath $DERIVED \
    -allowProvisioningUpdates "${TEAM_SETTING[@]}" CODE_SIGN_STYLE=Automatic \
    build > $LOGS/xcode.log 2>&1
XCODE_STATUS=$?
tail -20 $LOGS/xcode.log
# A failed build leaves the last one's .app behind: don't install that as if it were new.
if [ $XCODE_STATUS -ne 0 ]; then
    grep -m 5 -E "error|No space left" $LOGS/xcode.log
    echo "Xcode build failed; the full output is in $LOGS/xcode.log. Nothing was installed."
    exit 1
fi
APP=$(ls -d $DERIVED/Build/Products/Debug-iphoneos/*.app 2>/dev/null | head -1)
[ -n "$APP" ] || { echo "No .app built; see the Xcode output above."; exit 1; }

echo "== install"
# Installing over the app ends it, and ios_launch.sh ends anything left before launching.
xcrun devicectl device install app --device $UDID "$APP" || {
    echo "Install failed. If the phone asks, turn on Settings > Privacy & Security > Developer Mode, restart it, and run this again."
    exit 1
}
zsh $P/Tools/ios_launch.sh "$@"
