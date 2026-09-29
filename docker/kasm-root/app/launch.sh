#!/bin/bash
# Starts the Electron host. Run by /defaults/autostart (see there for why this
# lives in /app rather than in the autostart file itself).
cd /app

# The real Electron ELF binary, not node_modules/.bin/electron. That wrapper is
# a Node script, and Node 24 is deliberately kept off the global PATH so the
# base image's Node 18 stays intact for its /kclient audio component. Electron
# bundles its own Node runtime, so the binary needs nothing on PATH.
#
# --ozone-platform=x11: Skua embeds the game window by its X11 id; newer
#   Electron would pick Wayland by itself wherever one is available.
# --no-sandbox: Chromium's setuid sandbox does not work in this container
#   without extra privileges. The window is already confined to the container.
# --disable-dev-shm-usage: survive a small /dev/shm if shm_size is not raised.
# --enable-unsafe-swiftshader: with no GPU passed in, Chromium otherwise refuses
#   the silent software-WebGL fallback Ruffle's wgpu renderer depends on. With
#   /dev/dri passed in, the real GPU is used and this is inert.
#
# CHROMIUM_FLAGS: extra switches, space-separated, appended as-is, for GPU
# experiments without rebuilding the image. Do not use --use-angle=gl-egl with
# an Intel GPU under KasmVNC: it logs "No suitable EGL configs" (tested).
#
# With a GPU passed in, Chromium's ANGLE-on-GLX path asks the X server for the
# display refresh rate every frame. KasmVNC's Xvnc has no VidMode extension,
# so each attempt prints two harmless lines, ~35 pairs a second. Drop exactly
# those two from stderr; everything else passes through.
# shellcheck disable=SC2086
exec /app/node_modules/electron/dist/electron . \
  --ozone-platform=x11 \
  --no-sandbox \
  --disable-dev-shm-usage \
  --disable-gpu-sandbox \
  --enable-unsafe-swiftshader \
  $CHROMIUM_FLAGS \
  2> >(grep --line-buffered -v -e 'glXGetMscRateOML failed' -e 'extension "XFree86-VidModeExtension" missing' >&2)
