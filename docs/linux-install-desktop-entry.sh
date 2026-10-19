#!/bin/sh
# Adds VibeSkua and VibeSkua Manager to the applications menu, for this user
# only (~/.local/share/applications), pointing at this folder; run it again
# after moving the folder. --remove takes the entries out.
set -eu
dir=$(cd "$(dirname "$0")" && pwd)
apps="${XDG_DATA_HOME:-$HOME/.local/share}/applications"

if [ "${1:-}" = "--remove" ]; then
    rm -f "$apps/vibeskua.desktop" "$apps/vibeskua-manager.desktop"
    echo "Removed the VibeSkua menu entries."
    exit 0
fi

mkdir -p "$apps"
entry() { # <file> <name> <exec> <comment>
    cat > "$apps/$1" <<EOF
[Desktop Entry]
Type=Application
Name=$2
Comment=$4
Exec="$3"
Path=$dir
Icon=$dir/vibeskua.png
Terminal=false
Categories=Game;
EOF
}
entry vibeskua.desktop "VibeSkua" "$dir/VibeSkua" "AQW bot client"
entry vibeskua-manager.desktop "VibeSkua Manager" "$dir/VibeSkua Manager" "Start saved accounts as VibeSkua tabs"
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$apps" 2>/dev/null || true
echo "Added VibeSkua and VibeSkua Manager to the applications menu ($apps)."
