# VibeSkua Web

AQW running on [Ruffle](https://ruffle.rs) with the Skua ExternalInterface
bridge live, packaged as a browser-accessible container in the style of
`linuxserver/chrome`.

```bash
docker compose up -d vibeskua-web
# http://<host>:3000   (user: vibeskua, password: changeme — change these)
```

## Read this first: this is not the bot

It runs the **client**, not VibeSkua. Three layers, and only the first is done:

| Layer | Status |
| :--- | :--- |
| AQW client renders on Ruffle, bridge callbacks live | ✅ verified (Gitea run #698, 28/28) |
| You can log in and play | ⚠️ implemented here, **never tested** |
| The bot automates it | ⚠️ `Skua.Host` (`SKUA_HOST=1`), phase 1: scripts via an API, no UI |

`Skua.Core` — the actual bot brain: scripts, combat, quests and scheduling —
runs headless next to the page with `SKUA_HOST=1` (see "Skua" below). There
is no Skua UI yet; scripts are started through a local API.

See `docs/ruffle-test/README.md` for what has actually been proven, and
`DOCKER.md` for why the real client cannot be containerised as written.

## Status

**Ruffle's `socketProxy` works.** That was the one design assumption nothing
could confirm, and the second container run settled it: Ruffle routed a game
socket to the local bridge, exactly as `{host, port, proxyUrl}` predicted.

Confirmed working in the container: Electron under KasmVNC, Ruffle 0.6.0, the
live AQW client loading inside `skua.swf`, the ExternalInterface bridge
(`Externalizer::init done.`), the Auras API initialising, and socket routing
reaching the proxy.

Five bugs found and fixed so far, all mine:

| Symptom | Cause |
| :--- | :--- |
| `xz: Cannot exec` | Base image has no `xz`; use the `.tar.gz` Node tarball |
| `unzip` missing | `setup.js` shells out to it on Linux |
| `/kclient` `ERR_DLOPEN_FAILED` | Node 22 in `/usr/local` shadowed the base image's Node 18, whose native `pulseaudio2` needs `NODE_MODULE_VERSION 108` |
| `[proxy #1] refused asia.game.artix.com:5588` | Allowlist only covered `*.aq.com`; live servers also use `asia.game.artix.com` and `euro.aqw.artix.com`, which are two labels deep |
| WebGL software fallback refused | Chromium needs `--enable-unsafe-swiftshader` where there is no GPU |

The allowlist now accepts any depth under `aq.com` / `aqworlds.com` /
`artix.com`, and `main.js` additionally feeds it every `sIP` from the live
server list, so a new Artix domain needs no code change. The trailing anchor
still refuses `sock7.aq.com.evil.net`.

Still unverified: **login and gameplay end to end.** The socket now reaches the
bridge; whether SmartFoxServer traffic survives the round trip is the next
thing a real login attempt will answer.

### Expected noise

- `Error #1009 ... (accessing field: ModalStack)` ~30x/sec before login is a
  known bug in `Skua.AS3/skua/src/skua/module/QuestRequirementWiki.as:18`, not
  a container problem. It reads `game.ui.ModalStack` and guards the result but
  not `game.ui`, which is null until login. Fix: `if (!game.ui) return;` —
  needs the Flex SDK to recompile `skua.swf`.
- `hideme.swf ... 404` — the default background SWF; cosmetic.
- `SharedObject ... non-HTTPS origin` — the page is served over
  `http://127.0.0.1`; affects saved preferences, not login.
- A handful of AVM2 stubs (`Security.allowDomain`, `Dictionary` weak keys,
  `describeTypeJSON`, `Loader.close`/`unloadAndStop`).

## CPU usage

This container is CPU-hungry, from three separate causes.

### 1. Per-frame AVM2 exceptions (fixed)

Two Skua modules are enabled by default and both read `game.ui.ModalStack`
while guarding only the result, not `game.ui`:

- `QuestRequirementWiki.as:18`
- `QuestItemRates.as:12`

`game.ui` is null whenever no UI panel is open, so each throws an uncaught
AVM2 error on every `ENTER_FRAME` — ~30/sec each, every one formatted with a
stack trace and marshalled over Electron IPC. In one sample that was **22,530
of 22,601 log lines (99.7%, 2.2MB)**.

`DISABLE_MODULES` switches both off via the `modDisable` callback once the game
has loaded. `OptimizePlayers` guards the same access correctly
(`if (game.ui != null)`) and is left alone. The real fix is a null check in the
AS3, which needs the Flex SDK to recompile `skua.swf`.

### 2. Software rendering (not fixable in this container)

With no GPU, Ruffle's wgpu renderer falls back to SwiftShader and rasterises
every frame on the CPU — visible in the logs as
`GPU stall due to ReadPixels`. `RUFFLE_QUALITY=low` drops anti-aliasing and
bitmap smoothing, which is the cheapest saving available. A real GPU via
`devices: [/dev/dri:/dev/dri]` is the actual fix.

### 3. Ruffle itself

Ruffle's AVM2 is slower than Adobe's Flash Player, which shipped a JIT. This
is a known, upstream gap, and it is corroborated: Artix cite a Ruffle memory
leak as the blocker for putting their bigger games on it, and the aquastar
launcher reports poor FPS in crowded rooms, "mostly limited by Ruffle itself".

The JIT gap is upstream. But two AQW-specific leaks were Ruffle bugs, and
are fixed on the `aqw-loader-fixes` branch of the Ruffle clone:

- **`Loader.unloadAndStop()` was a stub** that only called `unload()`. AQW
  calls it on the map loader at every room change, and on every equipment
  slot when a player leaves (`World.closeLoader` → `ldr.unloadAndStop(true)`).
  `enterFrame` is a broadcast event, so the "unloaded" content kept running
  its per-frame code. Proven by test: unpatched Ruffle ran 4 frames of the
  unloaded content's handlers; patched runs 0.
- **`Loader.close()` was a no-op.** AQW calls it just before each of those
  `unloadAndStop`s. A player who left before their gear finished downloading
  still had it instantiated afterwards.

Both help most in crowded rooms and long sessions — the cases that were
slowest. One larger leak remains: weak-keyed `Dictionary` is treated as
strong, and AQW's `Game._colorCache` is keyed by avatar MovieClips whose
values reference the whole avatar, so departed players are never collected.
Fixing that needs ephemeron support in Ruffle's GC.

To run the patched Ruffle, build its web package
(`web/packages/selfhosted/dist`) and point at it:

```bash
# outside Docker
RUFFLE_WEB_DIR=/path/to/ruffle/web/packages/selfhosted/dist npm run setup

# in the image: zip that dist/, host it (e.g. a Gitea release asset), then
docker build -f docker/Dockerfile.kasm --build-arg RUFFLE_WEB_URL=<zip url> .
# or set the RUFFLE_WEB_URL repo variable for the Publish image workflow
```

### 4. Drawing less (patched Ruffle)

The biggest lever turned out to be rendering itself. Benchmarked on 2 cores
with SwiftShader (no GPU), against the live AQW client:

| Renderer | Scale | Max draw fps | Game ticks/s | CPU cores |
| :--- | :--- | :--- | :--- | :--- |
| wgpu-webgl | 1.0 | unlimited | 14.9 | **1.89** (pegged) |
| webgl | 0.75 | 15 | full speed | 1.35 |
| webgl | 0.5 | 10 | 60 | 0.74 |
| webgl | 0.5 | 5 | 60 | 0.46 |
| — | — | **0 (headless)** | **60** | **0.17** |

"Game ticks/s" is the point: once drawing stops saturating the CPU, the game
logic runs at full speed even while little or nothing is drawn. The patched
Ruffle adds two controls to do that -- `maxRenderFps` and `renderScale` -- and
the page exposes them three ways:

- env vars `RUFFLE_RENDERER`, `RENDER_SCALE`, `MAX_RENDER_FPS`
  (defaults: wgpu-webgl, 1.0, unlimited -- see the warning below)
- a control bar under the game: **Draw** Off/5/15/30/Max, **Resolution**
  50/75/100%
- `window.vibeskua.setRender({ fps, scale })` for automation, e.g.
  `{ fps: 0 }` while farming

With the official Ruffle release these do nothing and the bar stays hidden.

**What was tested in a logged-in Battleon** (2026-09-26, 7-10 players):

| Renderer | Draw | Resolution | Result |
| :--- | :--- | :--- | :--- |
| wgpu-webgl | Max | 100% | Loads fully |
| wgpu-webgl | **15** | 100% | Loads fully -- the frame cap is safe |
| **webgl** | Max | 100% | Avatars, HUD and player data missing |
| webgl + Ruffle `560f6f6f3` | Max | 100% | Loads and runs well; map background blank |
| webgl + Ruffle `b60c83526` | Max | 100% | Everything draws: map rasteriser, cooldown overlay, Static Player Art |

webgl failed because Ruffle's `BitmapData.draw()` raised an internal error on
backends that cannot render offscreen, and AQW calls it while setting up a
room: `RustError("Render backend does not support BitmapData.draw")`, then
`Error #1009 at Game/userTreeWrite()`. Fixed in Ruffle `560f6f6f3` (draw now
leaves the bitmap unchanged instead of aborting the script).

Ruffle `b60c83526` implements `BitmapData.draw` on webgl (it used to draw
nothing), so everything AQW draws into bitmaps now works there: the map
background rasteriser (`World.rebuildMapBMP`), the skill-cooldown overlay
(`World.coolDownAct`), aura icon fades and **Static Player Art**. Tested
in-game on an Intel GPU, frame rate unchanged. **Smooth Background** is back
to being the player's choice (Options > Game Settings); off, AQW draws the
map as one bitmap, which is the cheaper option.

With the **official** Ruffle release (no `RUFFLE_WEB_URL`), webgl still
cannot draw into bitmaps: the map background is blank unless Smooth
Background is on, and the cooldown overlay and Static Player Art do nothing.

**Recommended:** webgl, with Draw 15 for lower CPU or Max for smoothness, and
Draw Off while farming. wgpu-webgl draws filters (glows, shadows) that webgl
does not, but manages ~2 fps in a busy room; see [TESTING.md](TESTING.md).

### Knobs

| Setting | Default | Effect |
| :--- | :--- | :--- |
| `DISABLE_MODULES` | `QuestRequirementWiki,QuestItemRates` | Stops ~60 exceptions/sec |
| `ENABLE_MODULES` | empty | `DisableFX,HidePlayers` is the biggest in-game lever; changes what you see |
| `RUFFLE_QUALITY` | `low` | `low`/`medium`/`high` — AA costs CPU in software |
| `RUFFLE_RENDERER` | `wgpu-webgl` | `webgl` is lighter but experimental (see above) |
| `RENDER_SCALE` | `1` | Render resolution; 0.75 / 0.5 are cheaper |
| `MAX_RENDER_FPS` | `Infinity` | `0` = headless, e.g. `15` to cap |
| `cpus:` | `2.0` | Caps the burn so the host cannot thermal throttle |
| `devices: /dev/dri` | commented out | Real GPU rendering — the largest win by far |

The `cpus` cap does not make it faster; it stops it cooking your machine.

## How it fits together

```
KasmVNC (:3000) ──► Electron window
                      │
                      ├─ http://127.0.0.1:8770   static: index.html, ruffle/, skua.swf
                      │                          /servers → Artix API, fetched server-side
                      │
                      ├─ webRequest.onHeadersReceived
                      │    adds Access-Control-Allow-Origin to *.aq.com
                      │    (/game/api/* sends none: gameversion, login, servers)
                      │
                      └─ ws://127.0.0.1:8181/<host>/<port>
                           WebSocket↔TCP bridge to SmartFoxServer
```

`skua.swf` loads the real client from `game.aq.com`, then `Externalizer.as`
registers ~65 callbacks over ExternalInterface. The page answers `debug` and
`requestLoadGame`, and calls `loadClient()` back.

### Why the socket bridge exists

Ruffle in a browser context cannot open raw TCP, but AQW talks to
SmartFoxServer over plain TCP. Every `(host, port)` the SWF dials must be
declared in Ruffle's `socketProxy` and routed to a WebSocket. `index.html`
fetches the live server list and registers each `sIP`/`iPort`, plus port 843
for Flash's cross-domain policy.

`socket-proxy.js` only dials `*.aq.com` / `*.aqworlds.com`. Without that
allowlist the container would be an open TCP relay for anything that can reach
port 8181.

## Files

| File | Purpose |
| :--- | :--- |
| `main.js` | Electron host: static server, CORS fix, starts the bridge, opens the window |
| `socket-proxy.js` | WebSocket↔TCP relay, allowlisted to Artix hosts |
| `public/index.html` | Builds `socketProxy`, loads `skua.swf`, answers the bridge |
| `setup.js` | Downloads Ruffle (pinned `v0.6.0`), stages `skua.swf` |

`public/ruffle/`, `public/skua.swf` and `node_modules/` are generated and
gitignored.

## Running it outside Docker

```bash
cd web
npm install
npm run setup
npm start
```

Opens the same window on your desktop. Useful for debugging the bridge without
rebuilding the image.

## Remote control over the DevTools protocol

For scripted testing (Puppeteer: screenshots, input, calling the Skua bridge
and `window.vibeskua`). Off by default.

```yaml
    ports:
      - "3000:3000"
      - "192.168.2.20:9222:9222"   # the Docker host's LAN IP, not 0.0.0.0
    environment:
      REMOTE_DEBUG_PORT: "9222"
```

Check from another machine: `http://192.168.2.20:9222/json/version` should
return JSON. Use the IP, not a hostname: Chrome refuses DevTools requests whose
Host header is a name other than localhost. The `webSocketDebuggerUrl` it
returns already uses the address you asked on, so Puppeteer can connect with
it directly (`puppeteer.connect({ browserWSEndpoint })`).

**No authentication.** Anyone who can reach the port can drive the logged-in
session and read everything the page can. Keep it on the LAN, don't route it
through Caddy or any other reverse proxy, and remove it when you're not testing.

## Auto-login and recycling

Ruffle keeps every SWF it loads for the whole session. AQW loads the map and
every player's gear again on each room change, so memory grows ~45 MB per
change and frames slow down with it (measured with the patched Ruffle's
`player.debugStats()`: one more copy of the map SWF alive after every join).
Until that is fixed in Ruffle, the page can recycle itself:

| Env | Effect |
| :- | :- |
| `AQW_USER`, `AQW_PASS` | Log in automatically (`game.login`, then `clickServer`, as Skua does), and again after a disconnect or recycle. Pass the password as a secret. |
| `AQW_SERVER` | Server name to pick; the first online one if unset. |
| `RECYCLE_AFTER_MINUTES` | Reload the client after this long (0 = off). |
| `RECYCLE_AFTER_MAP_CHANGES` | ...or after this many map/room changes (0 = off). |

A recycle waits until the character is out of combat, reloads the page (which
frees all of Ruffle's memory), logs back in and returns to the same map and
cell. Recycling needs the credentials. `window.vibeskuaSession.status()` and
`.recycle()` are there for checking or forcing it.

The credentials are served only to the page itself (`/autologin`: the Host
must be `127.0.0.1:8770` and a custom header is required, so other origins
cannot read it) and are never logged.

Tested 2026-09-26: auto-login onto Twilly, then a recycle after 2 map changes
reloaded, logged back in and returned to yulgar (Enter); live GC objects went
from ~536k to ~198k and loaded SWFs from 384 to 32.

## Skua (SKUA_HOST=1)

With `SKUA_HOST=1` the image runs `Skua.Host` (`/opt/skua-host`) beside the
page: Skua.Core with no UI, driving `skua.swf` through `public/skua-bridge.js`
over a local WebSocket (`127.0.0.1:8790`). `main.js` starts it, restarts it if
it exits, and prefixes its output with `[skua]` in the container log.

Scripts are started through its control API (see `Skua.Host/README.md`),
which by default listens inside the container only:

```sh
docker exec vibeskua-web curl -s localhost:8791/status
docker exec vibeskua-web curl -s -X POST -d '' 'localhost:8791/script/start?path=/config/scripts/X.cs'
docker exec vibeskua-web curl -s -X POST -d '' localhost:8791/script/stop
```

or set `SKUA_SCRIPT` to start one as soon as the character is logged in.
Skua's settings and files live in `/config/.config/Skua` (kept across
recreates, like the rest of `/config`).

The API has **no authentication** and runs arbitrary C# as the bot. To use it
from the LAN, publish `8791` on a LAN IP only and set
`SKUA_API_PREFIX=http://+:8791/`; never route it through a reverse proxy.

A recycle (above) reloads the page, which drops the bridge for a few seconds;
a script running at that moment sees its calls fail. Leave recycling off while
running long scripts, or set it with that in mind.

## Before exposing it

`CUSTOM_USER` / `PASSWORD` in `docker-compose.yml` are `vibeskua` / `changeme`.
KasmVNC's HTTP basic auth is the only thing between the internet and a
logged-in game session. Change them, and put it behind a reverse proxy with TLS
rather than publishing port 3000 directly.
