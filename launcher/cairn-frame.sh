#!/usr/bin/env bash
# Steam Launch Options: bash "/absolute/path/to/cairn-frame.sh" %command%
set -eu

mode=ask
case "${1:-}" in
    --vr) mode=vr; shift ;;
    --normal) mode=normal; shift ;;
esac
if (( $# == 0 )); then
    echo "Expected the full Steam %command% after the launcher." >&2
    exit 2
fi
config_dir=${XDG_CONFIG_HOME:-$HOME/.config}/cairn-vr
config=$config_dir/frame-controls
if [[ -f $config ]]; then
    saved=$(cat -- "$config")
else
    saved=""
fi
if [[ $mode == ask ]]; then
    if command -v zenity >/dev/null && [[ -n ${DISPLAY:-}${WAYLAND_DISPLAY:-} ]]; then
        choice=$(zenity --list --title="Play CAIRN" --column="Play mode" --column="ID" --hide-column=2 --print-column=2 \
            "Play normally" normal "VR with thumbstick reach" stick \
            "VR with controller movement" reach "VR with pointing at the rock" point) || exit 0
        if [[ $choice != normal ]]; then
            mode=vr
            defaults=()
            for item in gaze markers lefthanded; do
                if [[ "|$saved|" == *"|$item|"* ]]; then defaults+=(TRUE); else defaults+=(FALSE); fi
            done
            saved=$(zenity --list --checklist --title="VR controls" --ok-label="Play in VR" \
                --column="Use" --column="ID" --column="Control" --hide-column=2 --print-column=2 \
                "${defaults[0]}" gaze "Select limbs with eye tracking" \
                "${defaults[1]}" markers "Place route markers while scouting" \
                "${defaults[2]}" lefthanded "Use left hand to reach and mark routes") || exit 0
            saved="$choice${saved:+|$saved}"
            mkdir -p -- "$config_dir"
            temporary=$(mktemp "$config_dir/.frame-controls.XXXXXX")
            printf '%s\n' "$saved" > "$temporary"
            mv -f -- "$temporary" "$config"
        else
            mode=normal
        fi
    else
        mode=normal
    fi
fi
if [[ $mode == normal ]]; then
    export CAIRN_VR=0 DOORSTOP_DISABLE=TRUE
    exec "$@"
fi

# Steam wraps Proton in its own runtime and can prepend additional helpers.
# Replace only the game executable. Preserve the runtime, its verb, and all
# arguments following the original exe exactly as Steam supplied them.
command_line=("$@")
found=false
for index in "${!command_line[@]}"; do
    arg=${command_line[$index]}
    normalized=${arg//\\//}
    if [[ ${normalized##*/} == [Cc][Aa][Ii][Rr][Nn].[Ee][Xx][Ee] ]]; then
        command_line[$index]="${arg:0:${#arg}-9}CairnVRFrame.exe"
        found=true
        break
    fi
done
if [[ $found != true ]]; then
    echo "Steam's command does not contain a Cairn.exe argument. Check CAIRN's launch options." >&2
    exit 2
fi
feature() {
    if [[ "|$saved|" == *"|$1|"* ]]; then printf '1'; else printf '0'; fi
}
export CAIRN_VR=1
export CAIRNVR_GAZE=${CAIRNVR_GAZE:-$(feature gaze)}
export CAIRNVR_REACH=${CAIRNVR_REACH:-$(feature reach)}
export CAIRNVR_MARKERS=${CAIRNVR_MARKERS:-$(feature markers)}
export CAIRNVR_POINT=${CAIRNVR_POINT:-$(feature point)}
export CAIRNVR_LEFTHANDED=${CAIRNVR_LEFTHANDED:-$(feature lefthanded)}
export WINEDLLOVERRIDES="${WINEDLLOVERRIDES:+$WINEDLLOVERRIDES;}winhttp=n,b"
unset DOORSTOP_DISABLE
exec "${command_line[@]}" -force-d3d11 --doorstop-enabled true
