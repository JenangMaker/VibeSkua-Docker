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
| The bot automates it | ❌ not started |

`Skua.Core` — the actual bot brain, 191 files of scripts, combat, quests and
scheduling — still has no `IFlashUtil` implementation pointing at this host.
Until it does, this is a way to play AQW in a browser, not to automate it.

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

## Before exposing it

`CUSTOM_USER` / `PASSWORD` in `docker-compose.yml` are `vibeskua` / `changeme`.
KasmVNC's HTTP basic auth is the only thing between the internet and a
logged-in game session. Change them, and put it behind a reverse proxy with TLS
rather than publishing port 3000 directly.
