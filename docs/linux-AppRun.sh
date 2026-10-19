#!/bin/sh
# AppRun of VibeSkua's AppImage (linux-release.yml): starts the app inside the
# mounted image. The Manager: "--manager", or an AppImage whose file name holds
# "Manager" (as the tar.gz has "VibeSkua Manager" beside "VibeSkua").
# The image is read-only; everything Skua keeps is in ~/.config as with the tar.gz.
here="$(dirname "$(readlink -f "$0")")"
case "$(basename "${ARGV0:-$APPIMAGE}")" in
  *Manager*|*manager*) exec "$here/VibeSkua/VibeSkua" --manager "$@" ;;
esac
exec "$here/VibeSkua/VibeSkua" "$@"
