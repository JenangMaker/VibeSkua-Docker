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

## Status: builds in progress

The image build has been exercised in CI (Gitea `Publish image`). Two bugs are
fixed so far, both in the base image's toolset rather than the app:

- `tar -xJ` failed with `xz: Cannot exec: No such file or directory` — the
  KasmVNC base has no `xz`. Now uses the `.tar.gz` Node tarball.
- `setup.js` shells out to `unzip` on Linux to unpack Ruffle; it was not
  installed. Added to the apt list.

**The container has still never run.** What is verified, in CI on
Linux, is the layer underneath: Ruffle loading `skua.swf`, the live AQW client
loading inside it, and all 28 probed ExternalInterface callbacks registering.

Specifically unverified:

- Electron starting under KasmVNC's X session
- Node 22 + Electron's shared libraries on `debianbookworm`
- **Ruffle's `socketProxy` schema.** This assumes `{host, port, proxyUrl}`.
  If Ruffle expects something else, login silently fails.
- Whether the WebSocket↔TCP bridge carries SmartFoxServer traffic correctly
- Login and gameplay end to end

Expect to iterate. The `Publish image` workflow builds it in CI, which is the
cheapest way to find the first round of breakage.

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
