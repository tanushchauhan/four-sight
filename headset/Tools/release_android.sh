#!/bin/zsh
# Builds a signed release APK from a fresh clone of the public repository, checks it, and makes a
# draft GitHub release with it. Nothing is published until you open the draft on GitHub and publish it.
#   zsh Tools/release_android.sh 1.0.0
# Built from the clone, not this folder, so the APK holds what anyone can build from the repository:
# only the placeholder videos, never local ones. Signed with your release keystore (not Unity's debug
# key), which you keep outside the repository and back up: an update signed with any other key won't
# install over a release. Its password is read from the macOS keychain:
#   security add-generic-password -a cardboardxr -s cardboardxr-keystore -w
# Needs gh (signed in) and the Unity version in the README. The first build in a fresh clone imports
# every asset, so it takes a while. Close the project in the Editor first.
set -euo pipefail
VERSION=${1:?usage: zsh Tools/release_android.sh 1.2.3}
REPO=${CARDBOARDXR_REPO:-brenpoly/CardboardXR}
U=${UNITY:-/Applications/Unity/Hub/Editor/6000.6.8f1/Unity.app/Contents/MacOS/Unity}
KEYSTORE=${CARDBOARDXR_KEYSTORE:-$HOME/.cardboardxr/release.keystore}
WORK=${TMPDIR:-/tmp}/cardboardxr-release
TAG=v$VERSION
APK=$WORK/CardboardXR-$VERSION.apk
SDK=${U:h:h:h:h}/PlaybackEngines/AndroidPlayer/SDK

[ -f $KEYSTORE ] || { echo "No keystore at $KEYSTORE"; exit 1; }
if pgrep -f "Unity.app/Contents/MacOS/Unity" >/dev/null; then echo "Unity is running; close it first"; exit 1; fi
if gh release view $TAG --repo $REPO >/dev/null 2>&1; then echo "$TAG already exists on $REPO"; exit 1; fi
PASS=$(security find-generic-password -a cardboardxr -s cardboardxr-keystore -w) ||
    { echo "No keystore password in the keychain (see the top of this script)"; exit 1; }

echo "== cloning $REPO"
rm -rf $WORK
mkdir -p $WORK
git clone --quiet --depth 1 https://github.com/$REPO.git $WORK/src
COMMIT=$(git -C $WORK/src rev-parse HEAD)

echo "== building $VERSION from ${COMMIT:0:7}"
# A folder that doesn't exist, so make_videos.sh copies nothing in.
CARDBOARDXR_VIDEOS=$WORK/none zsh $WORK/src/Tools/make_videos.sh
CARDBOARDXR_VERSION=$VERSION CARDBOARDXR_KEYSTORE=$KEYSTORE CARDBOARDXR_KEYSTORE_PASS=$PASS \
    $U -batchmode -projectPath $WORK/src -buildTarget Android \
    -executeMethod ProjectBootstrap.StereoBuilder.BuildAndroid -logFile $WORK/build.log || true
grep -E "\[Build\]|error CS" $WORK/build.log | head -20
grep -q "Build Succeeded" $WORK/build.log || exit 1
mv $WORK/src/Builds/CardboardXR.apk $APK

echo "== checking"
# Signed with the release key, and the only videos in it are the placeholders.
CERT=$(PASS=$PASS keytool -list -keystore $KEYSTORE -storepass:env PASS -alias ${CARDBOARDXR_KEY_ALIAS:-cardboardxr} -v |
    awk '/SHA256:/ {gsub(":", "", $2); print tolower($2); exit}')
SIGNED=$($SDK/build-tools/*/apksigner(On[1]) verify --print-certs $APK 2>/dev/null | awk '/certificate SHA-256 digest/ {print $NF; exit}')
[ "$SIGNED" = "$CERT" ] || { echo "Not signed with the release key ($SIGNED)"; exit 1; }
VIDEOS=$(unzip -Z1 $APK | grep -iE "\.(mp4|mov|m4v)$" | grep -v "/Sample_" || true)
[ -z "$VIDEOS" ] || { echo "Holds videos besides the placeholders:"; echo $VIDEOS; exit 1; }
SHA=$(shasum -a 256 $APK | cut -d' ' -f1)
echo "signed with ${CERT:0:16}…, $(( $(stat -f%z $APK) / 1000000 )) MB, sha256 $SHA"

echo "== draft release $TAG"
gh release create $TAG $APK --repo $REPO --target $COMMIT --draft --title "CardboardXR $VERSION" --notes "\
Android build of ${COMMIT:0:7}. Needs an ARCore phone (Android 10 or later, 64-bit, Vulkan).
Download the APK on the phone and open it; Android asks you to allow installing apps from your browser or file manager.

SHA-256: \`$SHA\`"
echo "Review and publish it at https://github.com/$REPO/releases"
