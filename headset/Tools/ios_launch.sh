#!/bin/zsh
# Launches the build already on the iPhone and copies its session log back.
#   zsh Tools/ios_launch.sh
#   zsh Tools/ios_launch.sh --ei dof 6 --ei passthrough 1
# Launch options are given as on Android (see LaunchOptions.cs): each "--ei name value" reaches the
# app as the environment variable CARDBOARDXR_name. Use this instead of ios_build_and_run.sh when
# nothing has changed and the app just needs to run again: after unlocking the phone, to re-read the
# log after a test, or to try other options. The phone must be unlocked, because iOS refuses to
# launch anything on a locked device.
set -u
BUNDLE=${CARDBOARDXR_BUNDLE_ID:-com.brenpoly.cardboardxr}
PREFIX=CARDBOARDXR_
LOGS=${TMPDIR:-/tmp}/cardboardxr
mkdir -p $LOGS

# --check only validates the options, so ios_build_and_run.sh can reject a typo before building.
CHECK_ONLY=0
if [ "${1:-}" = "--check" ]; then CHECK_ONLY=1; shift; fi

# The options as the JSON dictionary devicectl takes, e.g. {"CARDBOARDXR_dof": "6"}.
ENVIRONMENT=""
OPTIONS=("$@")
while [ $# -gt 0 ]; do
    if [ "$1" != "--ei" ] || [ $# -lt 3 ]; then
        echo "Launch options are \"--ei name value\" pairs; got: $*"
        exit 1
    fi
    case "$2" in
        (''|*[!A-Za-z0-9]*) echo "Option names are letters and digits, got \"$2\""; exit 1 ;;
    esac
    case "$3" in
        (-|'') echo "Option $2 needs a whole number, got \"$3\""; exit 1 ;;
        (*[!0-9-]*) echo "Option $2 needs a whole number, got \"$3\""; exit 1 ;;
    esac
    ENVIRONMENT+="${ENVIRONMENT:+, }\"$PREFIX$2\": \"$3\""
    shift 3
done
[ $CHECK_ONLY = 1 ] && exit 0

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

# --terminate-existing, because launching an app that's already running only brings it to the front,
# with its old options. (A separate "process terminate" needs a pid, not a bundle identifier: this
# script's first version asked it for one, it failed silently, and relaunches kept the old session.)
# And -e only with options: given an empty "{}", devicectl left the running app in place, options
# and all, even with --terminate-existing.
if [ -n "$ENVIRONMENT" ]; then
    LAUNCH=$(xcrun devicectl device process launch --device $UDID --terminate-existing -e "{$ENVIRONMENT}" $BUNDLE 2>&1)
    LAUNCHED="with {$ENVIRONMENT}"
else
    LAUNCH=$(xcrun devicectl device process launch --device $UDID --terminate-existing $BUNDLE 2>&1)
    LAUNCHED="without options"
fi
case "$LAUNCH" in
    # iOS won't start an app on a locked phone, and says so in among a wall of nested errors.
    *Locked*|*unlocked*) echo "The phone is locked. Unlock it and run: zsh Tools/ios_launch.sh ${OPTIONS[*]}"; exit 1 ;;
    *ERROR*) echo "$LAUNCH" | tail -5
        echo "On the first run, trust the developer under Settings > General > VPN & Device Management."
        exit 1 ;;
esac
echo "== launched $LAUNCHED"

echo "== waiting for the app to write its log"
sleep 25
# The equivalent of 'adb pull': the app's own Documents folder inside its sandbox.
xcrun devicectl device copy from --device $UDID --domain-type appDataContainer \
    --domain-identifier $BUNDLE --source Documents/session.log --destination $LOGS/ios-session.log 2>/dev/null \
    && { echo "== session log"; grep -E "\[Stereo\]|\[Demos\]|\[Pose\]|\[AR\]|\[Hands\]|Warning|Error|Exception" $LOGS/ios-session.log | grep -v stereoTargetEye | cut -c1-200 | head -25; } \
    || echo "No session log yet: $LOGS/ios-session.log"
