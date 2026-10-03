#!/bin/bash
# Starts VibeSkua in native game mode (docker/Dockerfile.native). Run by
# /defaults/autostart, as /app/launch.sh. No Electron and no Node: the Skua tab
# host opens a tab per account, and each tab's Skua starts Ruffle's desktop
# player for its game (Skua.Linux/NativeGame.cs) and logs it in
# (NativeSession.cs).
#
# Restarted here if it exits, with a backoff when it keeps dying young: what
# web/main.js did for Skua in the Electron image. The game players are the tabs'
# children and go with them.
export SKUA_GAME=native
export SKUA_BRIDGE_PREFIX="${SKUA_BRIDGE_PREFIX:-http://127.0.0.1:8790/}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

args=()
case "${SKUA_UI,,}" in 0|false|no) args+=(--headless) ;; esac

delay=2
while true; do
  started=$(date +%s)
  /opt/skua/Skua.App.Avalonia "${args[@]}"
  code=$?
  if (( $(date +%s) - started > 60 )); then delay=2; else delay=$(( delay * 2 > 60 ? 60 : delay * 2 )); fi
  echo "[host] Skua exited ($code); restarting in ${delay}s" >&2
  sleep "$delay"
done
