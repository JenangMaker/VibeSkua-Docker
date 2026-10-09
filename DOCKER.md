# VibeSkua in Docker

VibeSkua runs on Linux in a container and you use it from a browser: Skua's
full UI with the game embedded under its menu, several accounts in tabs, Army
Control and Grid View, as in the Windows app. Underneath, the game runs in
[Ruffle](https://ruffle.rs) (a Flash player in Rust/WebAssembly) inside
Electron, and Skua is the Windows client's UI ported to
[Avalonia](https://avaloniaui.net), on the same Skua.Core.

The image is built on LinuxServer's
[KasmVNC base](https://github.com/linuxserver/docker-baseimage-kasmvnc): the
container is a small desktop you open at `http://<host>:3000`.

> **Use at your own risk.** Botting is against AQW's terms of service; see the
> disclaimer in the [README](README.md).

- [Quick start](#quick-start)
- [Several accounts](#several-accounts)
- [Scripts](#scripts)
- [Keeping your settings](#keeping-your-settings)
- [Performance](#performance)
- [Environment variables](#environment-variables)
- [Web manager](#web-manager)
- [Advanced: control API and DevTools](#advanced-control-api-and-devtools)
- [Troubleshooting](#troubleshooting)
- [Building the image yourself](#building-the-image-yourself)

## Quick start

1. Save [`docker-compose.minimal.yml`](docker-compose.minimal.yml) as
   `docker-compose.yml` in an empty folder. It uses the published image,
   `ghcr.io/jenangmaker/vibeskua-web:latest`.
2. Set `PUID`/`PGID` to your user's (`id -u`, `id -g`) and change `PASSWORD`.
3. If the host has an Intel/AMD GPU (`ls /dev/dri` shows `renderD128`),
   uncomment the `devices` lines. Without it the game is drawn on the CPU,
   which is slow and heavy; see [Performance](#performance).
4. Start it and open the desktop:

   ```bash
   docker compose up -d
   # then browse to http://<this-host>:3000 and log in with CUSTOM_USER / PASSWORD
   ```

The first start takes a minute: Skua downloads the community scripts, then
its window opens with the AQW login screen in it. Log in there, or set
`AQW_USER_1` / `AQW_PASS_1` to have it done for you (and again after a
disconnect).

Only port **3000** (HTTP) is needed; **3001** serves the same over HTTPS with
a self-signed certificate. Anyone who can open it controls the bot and the
logged-in accounts, so keep it on your LAN, or put it behind a reverse proxy
with HTTPS and a login.

## Several accounts

Each tab is one account with its own Skua and its own game; **+** opens
another, **✕** closes one. The tab title becomes the character's name once
it is logged in.

- **Army Control** sends every tab the same command: start/stop scripts, load
  a script everywhere, the Army Scheduler (one playlist, run by every tab),
  log in/out all, jump all to a map or player, accept a quest, and the Misc
  Options toggles (Lag Killer, Hide Players, ...).
- **Grid View** shows every tab's game at once. Click a tab to go back.
- Tabs not on screen keep playing, slowed to 2 fps to save CPU.

To have accounts log in by themselves, number them. Skua opens one tab per
account at start:

```yaml
environment:
  AQW_USER_1: "first account"
  AQW_PASS_1: "..."
  AQW_USER_2: "second account"
  AQW_PASS_2: "..."
  AQW_SERVER: "Twilly"              # every tab's server (first online one if unset)
  AQW_SERVER_2: "Safiria"           # this tab's own server
  SKUA_SCRIPT_2: "Farm/GoldFarm"    # load this script in tab 2 at start
  SKUA_SCRIPT_AUTO_START_2: "1"     # and start it once logged in
```

`AQW_USER` / `AQW_PASS` (no number) also work for the first tab. Put the
passwords in an `.env` file or your orchestrator's secrets rather than in the
compose file. Only the game pages see them; Skua never does.

Accounts can also be added while it runs, through the tab host API (see
[Advanced](#advanced-control-api-and-devtools)). They are kept in
`/config/.config/vibeskua/accounts.json`, readable by the container user only,
and take a tab number the environment does not use: an account set in the
environment always wins and cannot be changed that way.

Every tab costs about as much memory and CPU as the first (a browser page
plus a Skua process, roughly 0.5-1 GB of RAM each), so size the container
for the number of accounts you run.

## Scripts

At start Skua syncs its Scripts folder with the community scripts
([auqw/Scripts](https://github.com/auqw/Scripts), branch `Skua`), as the
Windows app does. With **Options > Application > Auto Update Scripts** on it
downloads missing and outdated scripts silently; otherwise it asks
(**Update all** / **Only missing** / **Skip**). Scripts that fail to download
are listed in a pop-up.

- **Your own fork of the scripts:** `SKUA_SCRIPTS_REPO` (a GitHub or Gitea
  repository URL) and `SKUA_SCRIPTS_BRANCH`.
- **More script repositories:** `SKUA_SCRIPTS_EXTRA` syncs other repositories
  too, each into a folder of its own under Scripts. Write `folder=URL`,
  optionally with `#branch` (the repository's default branch otherwise); the
  folder defaults to the repository's name. Separate several with `;`, or use
  `SKUA_SCRIPTS_EXTRA_1`, `SKUA_SCRIPTS_EXTRA_2`, ... one each:

  ```yaml
  # UltrasLW's scripts include "Scripts/UltrasLW/...", so that is the folder
  SKUA_SCRIPTS_EXTRA: "UltrasLW=https://github.com/l0newolf12/UltrasLW"
  ```

  They are synced at start after the main scripts, by **Scripts > Reset
  Scripts**, and by the control API's `POST /scripts/update`. The `.cs` files that are missing or differ
  from the repository are downloaded without asking, local edits in that
  folder included; files no longer in the repository are left alone. The Script
  Loader's search (and the web manager's) finds them. `SKUA_SCRIPT_SYNC: "off"`
  skips them too.
- **Your own scripts folder:** mount it at `/config/.config/Skua/Scripts`.
  Skua then always asks before syncing, since "Update all" replaces outdated
  scripts, local edits included.
- **Load a script at start:** `SKUA_SCRIPT: "Farm/GoldFarm"` (a path in the
  scripts repository, or an absolute path). It shows in the Script Loader,
  ready to start; `SKUA_SCRIPT_AUTO_START: "1"` also starts it once logged
  in. Without a number both apply to **every tab** (tabs opened later with
  **+** too); `SKUA_SCRIPT_2`, `SKUA_SCRIPT_AUTO_START_2`, ... override them
  for one tab, and `SKUA_SCRIPT_2: "none"` gives tab 2 no script.
  Auto-start waits while the script sync is downloading (up to 15 minutes),
  so no tab compiles its script from half-updated files.
- **Pick up where you left off after a restart:** `SKUA_RESUME_SCRIPTS: "1"`.
  Each tab remembers the script it had loaded and whether it ran (in
  `/config/.config/vibeskua/resume.json`); after a redeploy, a container
  restart or a tab's Skua restarting, it loads that script again and starts
  it once logged in if it was running. Only for the same account, and only
  for tabs not given a script of their own (`SKUA_SCRIPT_N` or the Accounts
  page); it comes before the every-tab `SKUA_SCRIPT`. A script you stop
  counts as stopped after 2 minutes (a recycle or a relogin stops it for a
  moment), and then comes back loaded but not started. Closing a tab keeps
  its script loaded but not started.
- **Scripts that open their options window on every start** (UltrasLW's
  do, for one): tick **Don't open this window when this script starts** in
  that window, or in the web manager's Options dialog. The script then runs
  with its saved options, without asking. The Script Loader's **Options**
  button still opens the window, so the tick can be taken off there. The list
  is kept in `/config/.config/Skua/options/skip-options-window.txt`;
  `SKUA_SKIP_SCRIPT_OPTIONS: "1"` skips the window for every script.
- **Private room for CoreBots scripts:** `SKUA_ROOM_NUMBER: "9721"` puts
  every account in room 9721 (`SKUA_ROOM_NUMBER_2` for one tab). It is written
  into each account's CoreBots Options (Private Rooms on, that room number)
  when the account logs in, so it works for any CoreBots script; a room you
  set in CoreBots Options yourself lasts until the account next logs in.

## Keeping your settings

Mount `/config` (the minimal compose file does). Everything worth keeping is
in it:

| Path in the container | What |
| :--- | :--- |
| `/config/.config/Skua/Skua.settings.json` | Skua's options, hotkeys, theme |
| `/config/.config/Skua/options/` | CoreBots options, one `CBO_Storage(<character>).txt` each |
| `/config/.config/Skua/Scripts/` | the scripts |
| `/config/.config/Skua/plugins/`, `themes/` | plugins and themes |
| `/config/.config/vibeskua-web/` | the game pages' saved data (one partition per tab) |
| `/config/.config/vibeskua/accounts.json` | accounts added through the tab host API (holds their passwords) |

The folder on the host must belong to `PUID`:`PGID`. If it does not, Skua
cannot write there; see [Troubleshooting](#troubleshooting).

## Performance

**Give it the GPU.** With the host's Intel/AMD GPU passed in, the game draws
smoothly (around 35 fps in a busy Battleon on an Intel iGPU) at little CPU
cost. Without one it is drawn on the CPU at roughly 10 fps in a busy town
(one core, most of it). If `ls /dev/dri` on the host lists a `renderD128`,
add to the service:

```yaml
    devices:
      - /dev/dri:/dev/dri
```

`docker logs vibeskua` says at start whether it found the GPU (`[host] GPU
found` / `[host] no GPU passed in`). NVIDIA cards need the NVIDIA container
toolkit instead; that is untested here.

Without a GPU the image uses the `canvas` renderer, the lightest there is
then (the WebGL renderers would run on an emulated GPU: ~8 cores for ~2 fps
in a busy map), and draws at most 15 frames a second. The AQW Ruffle build
caches each shape once drawn, so a 10-player Battleon draws at about 10 fps
(it was ~1 fps without the cache). Canvas leaves out some effects (glows,
shadows), and it draws on the page's main thread, the one the game runs on,
so in a busy map drawing slows the game's own updates a little too. Quiet
maps draw faster.

**For a bot you are not watching, draw nothing:** `MAX_RENDER_FPS: "0"` (or
**Draw: Off** on the bar under the game). Then the game costs next to nothing
and runs at full speed; switch drawing back on when you want to look.

- `MAX_RENDER_FPS` sets how often the game is drawn; `0` draws nothing. With
  a GPU the default is unlimited.
- The game scales to fill its window, so a bigger window draws more pixels.
  `RENDER_SCALE: "0.75"` (or `"0.5"`) draws at a lower resolution.
- `RUFFLE_RENDERER`: `webgl` by default with a GPU, `canvas` without.
  `wgpu-webgl` draws every effect but is many times slower; only consider it
  with a GPU.
- These three can also be changed live from the bar under the game, which
  shows with `SHOW_DEBUG_PANEL: "1"` (or `?debug=1` on the game page).
- Tabs you are not looking at run at 2 frames per second with their game
  drawn at 1x1, as in the Windows VibeSkua; switching to a tab (or Grid View)
  puts it back. Map changes, moving and fighting are not slowed by this.
  `SKUA_HIDDEN_FPS` changes the rate.
- **Headless Mode** (Army Control > Misc Options, or its hotkey) does the
  same for a tab you *are* looking at: 1 frame per second, game not drawn,
  a notice in its place. Turn it off to see the game again.
- `ENABLE_MODULES: "DisableFX,HidePlayers"` switches on Skua's own
  performance modules.
- Ruffle keeps every SWF it ever loads (each map, every player's gear), so a
  long session grows. `RECYCLE_AFTER_MINUTES` (e.g. `"120"`) reloads the game
  when out of combat, logs back in and returns to the same map. It needs the
  account's login in the environment. A script that was running is restarted
  afterwards (CoreBots scripts carry on from their saved progress), as it is
  whenever the page logs an account back in because Skua did not.
- `cpus` in the compose file (2 in the minimal one) stops it from starving
  the rest of the host. Raise it for several accounts.

## Environment variables

LinuxServer's base image also takes its usual settings (`PUID`, `PGID`, `TZ`,
`CUSTOM_USER`, `PASSWORD`, `TITLE`, ...); see its documentation.

| Variable | Default | What |
| :--- | :--- | :--- |
| `AQW_USER_<N>`, `AQW_PASS_<N>` | | Account for tab N; logs it in automatically. `AQW_USER`/`AQW_PASS` also work for tab 1. |
| `AQW_SERVER`, `AQW_SERVER_<N>` | first online | Server to log in to, for every tab or for tab N. |
| `RECYCLE_AFTER_MINUTES`, `RECYCLE_AFTER_MAP_CHANGES` | off | Reload the game after this long / this many map changes (out of combat), then log back in and return. |
| `SKUA_TABS` | `1` | `0`: one Skua, no tabs. `N`: open N tabs at start (at least one per configured account). |
| `SKUA_SCRIPT`, `SKUA_SCRIPT_<N>` | | Script to load at start: every tab's / tab N's (`none`: no script for that tab). |
| `SKUA_SCRIPT_AUTO_START`, `SKUA_SCRIPT_AUTO_START_<N>` | `0` | `1`: also start it once logged in (every tab / tab N). |
| `SKUA_RESUME_SCRIPTS` | `0` | `1`: after a restart each tab loads the script it had again, and starts it if it was running (see above). |
| `RUFFLE_STRING_GC_DEBT` | on | Big strings (1 KB and up) count toward when the game's garbage collector runs. Without it, a script that reads the bank on an account with a big bank (each read a ~1 MB string, about 3 a second) grew the player from 0.5 to 1.1 GB in minutes. `0` turns it off. |
| `SKUA_ROOM_NUMBER`, `SKUA_ROOM_NUMBER_<N>` | unset (CoreBots Options) | Private room number (1-999999) CoreBots scripts use, for every tab / tab N. |
| `SKUA_SKIP_SCRIPT_OPTIONS` | `0` | `1`: a starting script's options window never opens; it runs with its saved options (per script: the window's checkbox, see [Scripts](#scripts)). |
| `SKUA_SCRIPT_SYNC` | `auto` | `auto` (follow Skua's options), `ask`, or `off`. |
| `SKUA_SCRIPTS_REPO`, `SKUA_SCRIPTS_BRANCH` | `https://github.com/auqw/Scripts`, `Skua` | Where scripts sync from (GitHub or Gitea). |
| `SKUA_SCRIPTS_EXTRA`, `SKUA_SCRIPTS_EXTRA_<N>` | none | More repositories to sync, each `folder=URL[#branch]` into Scripts/folder; `;` between several (see [Scripts](#scripts)). |
| `SKUA_HOST` | `1` | `0`: the game only, without Skua. |
| `SKUA_UI` | `1` | `0`: Skua without windows, driven through its control API only. |
| `SKUA_EMBED_GAME` | `1` | `0`: the game in its own window below Skua's instead of inside it (no tabs). |
| `RUFFLE_RENDERER` | `webgl` with a GPU, `canvas` without | `wgpu-webgl` draws every effect but is much heavier; see [Performance](#performance). |
| `RUFFLE_QUALITY` | `low` | `low`, `medium`, `high`. |
| `RENDER_SCALE` | `1` | Fraction of the window's resolution to draw at. |
| `SHOW_DEBUG_PANEL` | `0` | `1` shows the render controls and log under the game (the log goes to the container log either way; `?debug=1` on the page URL shows them for that page). |
| `SKUA_HIDDEN_FPS` | `2` | Game frame rate of tabs not on screen (1-60). |
| `SKUA_HEADLESS` | unset | Headless Mode for a tab that has not been switched yet: `1` on, `0` off. Each tab remembers its last Headless Mode across restarts and redeploys (in `headless-tab<N>` next to `accounts.json`); this only sets the start. |
| `SKUA_DASHBOARD` | auto | Bot dashboard (kills, drops, quests, deaths, relogins, time) beside the game: `1` always, `0` never; auto shows it when the window is wide enough. |
| `MAX_RENDER_FPS` | unlimited with a GPU, `15` without | Frames drawn per second; `0` draws nothing. |
| `ENABLE_MODULES`, `DISABLE_MODULES` | `""`, `QuestRequirementWiki,QuestItemRates` | Skua modules to switch on / off once the game loads. |
| `SKUA_API_PREFIX` | `http://127.0.0.1:8791/` | Where the first tab's control API listens (see below). |
| `SKUA_HOST_API_PREFIX` | `http://127.0.0.1:8789/` | Where the tab host API listens (see below). |
| `SKUA_API_TOKEN` | unset | When set, every control API (the tabs' and the tab host's) requires it: `Authorization: Bearer <token>`. |
| `VIBESKUA_ACCOUNTS_FILE` | `/config/.config/vibeskua/accounts.json` | Accounts added through the tab host API. |
| `REMOTE_DEBUG_PORT` | off | Chrome DevTools port (see below). |

## Web manager

[VibeSkua Manager](manager/README.md) is a web page for checking on and
controlling the bots from a phone or another PC, without opening the desktop:
every account's status, map, level, script and stats, what it is fighting and
its quests' progress, with its CPU and memory; script and Army controls,
loading scripts by search or by folder, each script's options and each tab's
Skua options, the live logs, accounts added while it runs, the container's
resources, and a streamer mode for showing the page. It is a separate small container
(`ghcr.io/jenangmaker/vibeskua-manager`) with its own login, reachable from your
LAN only by default.

Add it next to VibeSkua in the same compose file:

```yaml
services:
  vibeskua:
    # ... as before, plus:
    environment:
      SKUA_HOST_API_PREFIX: "http://+:8789/"
      SKUA_API_TOKEN: "${SKUA_API_TOKEN}"

  vibeskua-manager:
    image: ghcr.io/jenangmaker/vibeskua-manager:latest
    environment:
      MANAGER_PASSWORD: "${MANAGER_PASSWORD}"
      VIBESKUA_URL: "http://vibeskua:8789"
      VIBESKUA_TOKEN: "${SKUA_API_TOKEN}"
    ports:
      - "192.168.1.10:3040:3040"
    restart: unless-stopped
```

Put both secrets in an `.env` file next to it (`SKUA_API_TOKEN` a long random
string, for example from `openssl rand -hex 32`), then open
`http://192.168.1.10:3040`. Port 8789 needs no publishing when both are in the
same compose file. How to use it, all its settings and its security are in
[manager/README.md](manager/README.md).

## Advanced: control API and DevTools

Neither needs a port unless you want it. **DevTools has no authentication**,
and the control APIs have none unless `SKUA_API_TOKEN` is set. Whoever reaches
them controls the bot, so keep them on a LAN address, never on the internet.

- **Control API** (start/stop scripts, status, logs, Army commands): each tab's
  Skua has one inside the container, tab 1 on port 8791, tab N on
  `8791 + 10*(N-1)`. From the host:

  ```bash
  docker exec vibeskua curl -s localhost:8791/status
  docker exec vibeskua curl -s -X POST -d '' 'localhost:8791/script/start?path=Farm/GoldFarm'
  ```

  To reach tab 1's from your LAN, set `SKUA_API_PREFIX: "http://+:8791/"` and
  publish it on a LAN address only: `"192.168.1.10:8791:8791"`. The routes
  are listed in [Skua.Host/README.md](Skua.Host/README.md) and
  [Skua.Linux/ArmyApi.cs](Skua.Linux/ArmyApi.cs). A POST needs a body, even
  an empty one (`-d ''`), or it is refused with 411.
- **Tab host API** (port 8789): the whole instance through one port. It lists
  the tabs with their CPU and memory, opens, restarts and closes tabs, shows
  the container's resources, manages the accounts added at run time, sends
  Army commands to every tab, and passes `/tabs/<N>/api/...` on to tab N's
  control API. This is what a remote manager uses. To publish it:

  ```yaml
  environment:
    SKUA_HOST_API_PREFIX: "http://+:8789/"
    SKUA_API_TOKEN: "${SKUA_API_TOKEN}"   # a long random string, from .env
  ports:
    - "192.168.1.10:8789:8789"
  ```

  ```bash
  curl -s -H "Authorization: Bearer $SKUA_API_TOKEN" http://192.168.1.10:8789/tabs
  curl -s -H "Authorization: Bearer $SKUA_API_TOKEN" http://192.168.1.10:8789/tabs/2/api/log?type=script
  curl -s -H "Authorization: Bearer $SKUA_API_TOKEN" -X PUT http://192.168.1.10:8789/accounts/4 \
    -d '{"user":"name","pass":"...","server":"Twilly","script":"Farm/GoldFarm","autoStart":true}'
  ```

  The routes are listed in
  [Skua.App.Avalonia/TabHostWindow.Api.cs](Skua.App.Avalonia/TabHostWindow.Api.cs).
  Passwords can be set but are never returned.
- **Chrome DevTools** (drive the game pages with Puppeteer and the like):
  `REMOTE_DEBUG_PORT: "9222"` and `"192.168.1.10:9222:9222"`.

## Troubleshooting

- **"Skua cannot write to its Scripts folder" / "Access denied"**: the mounted
  folder does not belong to the container's user. Set `PUID`/`PGID` to the
  folder's owner (`stat -c '%u:%g' <folder>`) or `chown` the folder to them.
- **A blank grey area instead of the game**: the game window is still
  starting (it takes a few seconds after Skua's window), or crashed and is
  being reopened; check `docker logs vibeskua`.
- **Very slow, or the host fans spin up**: see [Performance](#performance);
  above all, pass the GPU in.
- **Logs**: `docker logs -f vibeskua`. Lines start with `[skua]` (Skua; `[tab N]`
  for other tabs), `[page]` / `[page N]` (the game pages) and `[host]`.

## Building the image yourself

```bash
docker build -f docker/Dockerfile.kasm -t vibeskua-web .
```

By default the image uses the official Ruffle release. VibeSkua works best
with the patched Ruffle build ([JenangMaker/ruffle](https://github.com/JenangMaker/ruffle),
branch `aqw-loader-fixes`: Loader and renderer fixes AQW needs, and the
renderer/fps controls), which the published image uses. Pass a zip of its web
build as `RUFFLE_WEB_URL`:

```bash
docker build -f docker/Dockerfile.kasm \
  --build-arg RUFFLE_WEB_URL=https://github.com/JenangMaker/ruffle/releases/download/aqw-latest/ruffle-aqw-selfhosted.zip \
  -t vibeskua-web .
```

The published image is built by `.github/workflows/publish-image.yml` (run
it from the Actions tab, or push a `v*` tag); the Ruffle zip comes from that
fork's `aqw.yml` workflow.

If that download needs a login, pass it as a BuildKit secret, never as a
build argument (those stay in the image):
`--secret id=ruffle_web_auth,env=RUFFLE_WEB_AUTH` with `RUFFLE_WEB_AUTH=user:token`.

How the pieces fit together: [web/README.md](web/README.md) (the game page and
Electron), [Skua.App.Avalonia/README.md](Skua.App.Avalonia/README.md) (Skua's
UI, tabs, script sync) and [Skua.Host/README.md](Skua.Host/README.md) (the
control API). Building the **Windows** client in a container is covered in
[docs/BUILD-DOCKER.md](docs/BUILD-DOCKER.md).
