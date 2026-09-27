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
- [Advanced: control API and DevTools](#advanced-control-api-and-devtools)
- [Troubleshooting](#troubleshooting)
- [Building the image yourself](#building-the-image-yourself)

## Quick start

1. Save [`docker-compose.minimal.yml`](docker-compose.minimal.yml) as
   `docker-compose.yml` in an empty folder. It uses the published image,
   `ghcr.io/jenangmaker/vibeskua-web:latest`.
2. Set `PUID`/`PGID` to your user's (`id -u`, `id -g`) and change `PASSWORD`.
3. Start it and open the desktop:

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
- **Your own scripts folder:** mount it at `/config/.config/Skua/Scripts`.
  Skua then always asks before syncing, since "Update all" replaces outdated
  scripts, local edits included.
- **Load a script at start:** `SKUA_SCRIPT: "Farm/GoldFarm"` (a path in the
  scripts repository, or an absolute path). It shows in the Script Loader,
  ready to start; `SKUA_SCRIPT_AUTO_START: "1"` also starts it once logged
  in. Per tab: `SKUA_SCRIPT_2`, `SKUA_SCRIPT_AUTO_START_2`, ...

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

The folder on the host must belong to `PUID`:`PGID`. If it does not, Skua
cannot write there; see [Troubleshooting](#troubleshooting).

## Performance

Without a GPU, Ruffle draws the game on the CPU, and AQW in software keeps a
couple of cores busy per account.

- **Give it the GPU** if the host has one (Intel/AMD):
  `devices: ["/dev/dri:/dev/dri"]`. That is the biggest improvement.
- `RUFFLE_RENDERER: "webgl"` is much lighter than the default `wgpu-webgl`,
  at the cost of some effects (skill cooldown shading, aura fades).
- `MAX_RENDER_FPS` caps how often the game is drawn (the game itself still
  runs at full speed); `0` draws nothing at all. `RENDER_SCALE: "0.75"` draws
  at a lower resolution. All three can also be changed live from the bar
  under the game.
- `ENABLE_MODULES: "DisableFX,HidePlayers"` switches on Skua's own
  performance modules.
- Ruffle keeps every SWF it ever loads (each map, every player's gear), so a
  long session grows. `RECYCLE_AFTER_MINUTES` (e.g. `"120"`) reloads the game
  when out of combat, logs back in and returns to the same map. It needs the
  account's login in the environment.
- `cpus: 4` in the compose file stops it from starving the rest of the host.

## Environment variables

LinuxServer's base image also takes its usual settings (`PUID`, `PGID`, `TZ`,
`CUSTOM_USER`, `PASSWORD`, `TITLE`, ...); see its documentation.

| Variable | Default | What |
| :--- | :--- | :--- |
| `AQW_USER_<N>`, `AQW_PASS_<N>` | | Account for tab N; logs it in automatically. `AQW_USER`/`AQW_PASS` also work for tab 1. |
| `AQW_SERVER`, `AQW_SERVER_<N>` | first online | Server to log in to, for every tab or for tab N. |
| `RECYCLE_AFTER_MINUTES`, `RECYCLE_AFTER_MAP_CHANGES` | off | Reload the game after this long / this many map changes (out of combat), then log back in and return. |
| `SKUA_TABS` | `1` | `0`: one Skua, no tabs. `N`: open N tabs at start (at least one per configured account). |
| `SKUA_SCRIPT`, `SKUA_SCRIPT_<N>` | | Script to load at start (tab 1 / tab N). |
| `SKUA_SCRIPT_AUTO_START`, `SKUA_SCRIPT_AUTO_START_<N>` | `0` | `1`: also start it once logged in. |
| `SKUA_SCRIPT_SYNC` | `auto` | `auto` (follow Skua's options), `ask`, or `off`. |
| `SKUA_SCRIPTS_REPO`, `SKUA_SCRIPTS_BRANCH` | `https://github.com/auqw/Scripts`, `Skua` | Where scripts sync from (GitHub or Gitea). |
| `SKUA_HOST` | `1` | `0`: the game only, without Skua. |
| `SKUA_UI` | `1` | `0`: Skua without windows, driven through its control API only. |
| `SKUA_EMBED_GAME` | `1` | `0`: the game in its own window below Skua's instead of inside it (no tabs). |
| `RUFFLE_RENDERER` | `wgpu-webgl` | `webgl` is lighter; see [Performance](#performance). |
| `RUFFLE_QUALITY` | `low` | `low`, `medium`, `high`. |
| `RENDER_SCALE` | `1` | Fraction of the window's resolution to draw at. |
| `MAX_RENDER_FPS` | `Infinity` | Frames drawn per second; `0` draws nothing. |
| `ENABLE_MODULES`, `DISABLE_MODULES` | `""`, `QuestRequirementWiki,QuestItemRates` | Skua modules to switch on / off once the game loads. |
| `SKUA_API_PREFIX` | `http://127.0.0.1:8791/` | Where the first tab's control API listens (see below). |
| `REMOTE_DEBUG_PORT` | off | Chrome DevTools port (see below). |

## Advanced: control API and DevTools

Neither needs a port unless you want it, and **neither has any
authentication**. Whoever reaches them controls the bot, so keep them on a
LAN address, never on the internet.

- **Control API** (start/stop scripts, status, logs, Army commands): each tab's
  Skua has one inside the container, tab 1 on port 8791, tab N on
  `8791 + 10*(N-1)`. From the host:

  ```bash
  docker exec vibeskua curl -s localhost:8791/status
  docker exec vibeskua curl -s -X POST 'localhost:8791/script/start?path=Farm/GoldFarm'
  ```

  To reach tab 1's from your LAN, set `SKUA_API_PREFIX: "http://+:8791/"` and
  publish it on a LAN address only: `"192.168.1.10:8791:8791"`. The routes
  are listed in [Skua.Host/README.md](Skua.Host/README.md) and
  [Skua.Linux/ArmyApi.cs](Skua.Linux/ArmyApi.cs).
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
  above all the GPU and `RUFFLE_RENDERER: "webgl"`.
- **Logs**: `docker logs -f vibeskua`. Lines start with `[skua]` (Skua; `[tab N]`
  for other tabs), `[page]` / `[page N]` (the game pages) and `[host]`.

## Building the image yourself

```bash
docker build -f docker/Dockerfile.kasm -t vibeskua-web .
```

By default the image uses the official Ruffle release. VibeSkua works best
with the patched Ruffle build (Loader and renderer fixes AQW needs, and the
renderer/fps controls). Pass a zip of its web build as `RUFFLE_WEB_URL`:

```bash
docker build -f docker/Dockerfile.kasm \
  --build-arg RUFFLE_WEB_URL=https://github.com/JenangMaker/ruffle/releases/download/<tag>/ruffle-web.zip \
  -t vibeskua-web .
```

If that download needs a login, pass it as a BuildKit secret, never as a
build argument (those stay in the image):
`--secret id=ruffle_web_auth,env=RUFFLE_WEB_AUTH` with `RUFFLE_WEB_AUTH=user:token`.

How the pieces fit together: [web/README.md](web/README.md) (the game page and
Electron), [Skua.App.Avalonia/README.md](Skua.App.Avalonia/README.md) (Skua's
UI, tabs, script sync) and [Skua.Host/README.md](Skua.Host/README.md) (the
control API). Building the **Windows** client in a container is covered in
[docs/BUILD-DOCKER.md](docs/BUILD-DOCKER.md).
