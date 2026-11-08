#!/bin/zsh
# Runs the EditMode tests, builds the app (Core, the lobby and the demos), installs it on the phone and launches it, then
# prints ~30 s of the app's tagged logs, the display's refresh-rate votes and the phone's
# temperatures. Extra arguments go to `am start`, for launch options:
#   zsh Tools/build_and_run.sh --ei dof 6 --ei passthrough 1
# Options: demo N, dof 3|6, passthrough 1, arView 1, boundaryCm N, handAxes 1..8, pinchStudy 1,
# imuPosition 0, nativeImu 0, tracker 1, predictionMs N, fps N.
# For heat: renderScale N (percent, e.g. 70), msaa 1|2|4|8, handHz N (0 = every image).
# Close the project in the Editor first (batch mode needs the project lock). Logs go to $LOGS.
# USB drops are common: if install doesn't print "Success", run `adb kill-server; adb start-server`
# and install again, since the launch that follows would otherwise start the previous build.
set -u
P=${0:A:h:h}
# Another Unity 6000.6 install: UNITY=/path/to/Unity.app/Contents/MacOS/Unity zsh Tools/...
U=${UNITY:-/Applications/Unity/Hub/Editor/6000.6.8f1/Unity.app/Contents/MacOS/Unity}
# Another app id (an iOS bundle id belongs to the Apple team that first signed it):
#   CARDBOARDXR_BUNDLE_ID=com.example.cardboardxr zsh Tools/...
PKG=${CARDBOARDXR_BUNDLE_ID:-com.brenpoly.cardboardxr}
LOGS=${TMPDIR:-/tmp}/cardboardxr
mkdir -p $LOGS
cd $P || exit 1
if pgrep -f "Unity.app/Contents/MacOS/Unity" >/dev/null; then echo "Unity is running; close it first"; exit 1; fi

echo "== EditMode tests"
rm -f $LOGS/tests.xml
$U -batchmode -projectPath $P -runTests -testPlatform EditMode -testResults $LOGS/tests.xml -logFile $LOGS/tests.log
if [ ! -f $LOGS/tests.xml ]; then grep -E "error CS|Exception" $LOGS/tests.log | head -20; exit 1; fi
python3 - $LOGS/tests.xml <<'EOF' || exit 1
import sys, xml.etree.ElementTree as ET
root = ET.parse(sys.argv[1]).getroot()
print(f'total={root.get("total")} passed={root.get("passed")} failed={root.get("failed")}')
for case in root.iter('test-case'):
    if case.get('result') == 'Failed':
        print('##', case.get('fullname'), (case.findtext('failure/message') or '').strip()[:400])
sys.exit(0 if root.get('failed') == '0' else 1)
EOF

echo "== build"
zsh $P/Tools/make_videos.sh || exit 1
# Removed first: a build that never runs would otherwise leave the last one's log to report success.
rm -f $LOGS/build.log
$U -batchmode -projectPath $P -buildTarget Android -executeMethod ProjectBootstrap.StereoBuilder.BuildAndroid -logFile $LOGS/build.log
grep -E "\[Build\]|error CS" $LOGS/build.log | head -20
grep -q "Build Succeeded" $LOGS/build.log || exit 1

echo "== install"
adb start-server >/dev/null 2>&1
# Stop it first: installing over the running app and then launching crashes Unity's activity.
adb shell am force-stop $PKG
# Stop unless it installs: launching anyway would start the build already on the phone, and its logs
# would look like the new one's. USB drops are common, so this happens.
INSTALL=$(adb install -r Builds/CardboardXR.apk 2>&1 | tail -1)
echo "$INSTALL"
case "$INSTALL" in
    *Success*) ;;
    *) echo "Install failed; not launching. Run 'adb kill-server; adb start-server', check 'adb devices', then run this again."; exit 1 ;;
esac
adb logcat -c
adb shell am start -W -n $PKG/com.unity3d.player.UnityPlayerGameActivity "$@" | grep -E "Status|LaunchState"
sleep 32
adb logcat -d -s Unity:V AndroidRuntime:E | grep -E "\[Demos\]|\[AR\]|\[Pose\]|\[Room\]|\[Anchor\]|\[Hands\]|\[Pinch\]|\[Perf\]|\[Passthrough\]|\[Frames\]|\[Hitch\]|\[Stereo\] (Head|Preferred)|Exception|FATAL" | grep -v AssetPackManager | cut -c1-300 | head -40
echo "== display votes"
adb shell dumpsys display 2>/dev/null | grep -A14 "mVotesByDisplay" | grep -E "PRIORITY" | cut -c1-160 | head -10
echo "== temperatures"
adb shell dumpsys thermalservice 2>/dev/null | sed -n '/Current temperatures from HAL:/,/Current cooling/p' | grep -oE "mValue=[0-9.]+, mType=[0-9]+, mName=[A-Za-z_-]+" | grep -iE "skin|battery|cpu|gpu" | head -8
echo "== the whole session log: adb pull /storage/emulated/0/Android/data/$PKG/files/session.log"
