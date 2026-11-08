#!/bin/zsh
# Copies your own videos into Assets/StreamingAssets so local builds bundle them. Git ignores them
# (see .gitignore): only the two small placeholder clips are in the repository. The build scripts
# run this first. Videos come from CARDBOARDXR_VIDEOS, by default a "360" folder beside the project.
# Up to 4320 wide (native 4K) they're copied as they are; wider ones (8K, too much for the phones'
# decoders) are scaled to 4K at 80 Mbit/s. Name tags set the format (VideoFormat.FromFileName).
#   zsh Tools/make_videos.sh            # copies what's missing
#   zsh Tools/make_videos.sh --force    # copies them all again
set -euo pipefail
P=${0:A:h:h}
SOURCE=${CARDBOARDXR_VIDEOS:-${P:h}/360}
DEST=$P/Assets/StreamingAssets
FORCE=0
[ "${1:-}" = "--force" ] && FORCE=1
[ -d $SOURCE ] || exit 0

for input in $SOURCE/*.(mp4|mov|m4v)(N); do
    output=$DEST/${input:t}
    if [ -f $output ] && [ $FORCE = 0 ]; then
        continue
    fi
    # Just the number: csv output adds a comma when the stream carries side data (spherical metadata).
    width=$(ffprobe -v error -select_streams v:0 -show_entries stream=width -of default=nw=1:nk=1 $input | head -1)
    if [ $width -le 4320 ]; then
        echo "== copying ${input:t} into StreamingAssets"
        cp $input $output.part
    else
        echo "== scaling ${input:t} ($width wide) to 4K in StreamingAssets"
        # The hardware encoder; hvc1 so Apple's players take it, the index first so it opens at once.
        ffmpeg -v error -stats -y -i $input -vf "scale=3840:1920:flags=lanczos,format=yuv420p" \
            -c:v hevc_videotoolbox -b:v 80M -tag:v hvc1 -c:a copy -movflags +faststart -f mp4 $output.part
    fi
    mv $output.part $output
done
