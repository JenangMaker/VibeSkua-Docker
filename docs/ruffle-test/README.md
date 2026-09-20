# Ruffle bridge test

Reproduces the experiment that answers: **can Skua's Flash bridge run on
[Ruffle](https://ruffle.rs) instead of the dead Flash ActiveX control?**

Short answer: **yes, in a browser-class host.** This harness proves it by
driving the real `skua.swf` against the live AQW client and exercising the same
`IFlashUtil` surface `Skua.Core` uses.

## Run it

Needs Node 18+ (tested on 24) and a network connection. ~100MB of downloads on
first run, all inside this folder.

```bash
cd docs/ruffle-test
npm install     # Electron
npm run setup   # Ruffle web build + stages skua.swf
npm test        # runs the harness, ~90s
```

Results land in `report.txt`. Watch the console for `[host]` and `[page]` lines
while it runs.

To watch it happen in a visible window:

```bash
# PowerShell
$env:SHOW_WINDOW=1; npm test
# bash
SHOW_WINDOW=1 npm test
```

`RUN_SECONDS` (default 90) controls how long it runs before quitting.

## Optional: testing login

By default the harness stops short of authenticating, and everything above
still passes. To also test **login on Ruffle**, provide credentials one of two
ways — both keep them off the network, out of git, and out of `report.txt`.

**Use a throwaway / alt account.** Two independent reasons: automating AQW can
get an account banned whatever the host is, and this is an experimental Flash
runtime that may misbehave mid-session.

### Option A — local file (repeatable runs)

Create `docs/ruffle-test/credentials.local.json`:

```json
{ "username": "your_alt_account", "password": "..." }
```

It is gitignored (verified), read only by `main.js` on localhost, and the
password is scrubbed from `report.txt` and all console output before either is
written.

### Option B — environment variables

```bash
# PowerShell
$env:AQW_USER="your_alt_account"; $env:AQW_PASS="..."; npm test
# bash
AQW_USER=your_alt_account AQW_PASS=... npm test
```

Slightly weaker than Option A — env vars are visible to other processes owned
by your user and can end up in shell history.

### Option C — just type it in

```bash
# PowerShell
$env:SHOW_WINDOW=1; npm test
```

The real AQW login screen renders in the Electron window. Type the credentials
yourself. Nothing is stored anywhere, by anyone. Best choice for a one-off
"does login work at all" check.

### What the login step does

Mirrors `ScriptServers`'s `[MethodCallBinding("login", GameFunction = true)]`:

```js
player.callGameFunction('login', username, password);
// then polls isLoggedIn() / isNull("world") for 60s
```

On success you should see `RESULT: LOGGED IN ON RUFFLE`, followed by
`world.strMapName`, your character level, and a `getMonsters()` sample — which
would mean the bot's read path works against a live logged-in session.

**Never** paste credentials into a chat window, a commit, or a script body.

## What a good run looks like

```
[host] CORS header injection installed for *.aq.com
[2.3s] AS3->JS debug: Externalizer::init done.
[2.3s] AS3->JS: requestLoadGame() received
[3.3s] CALL ok: isTrue() -> "true"
[3.3s] CALL ok: loadClient() -> undefined
[33.3s] CALL ok: getGameObject("params.sURL") -> "https://game.aq.com/game/"
[33.3s] RESULT: GAME SWF LOADED - cross-SWF reflection works
[33.3s] CALL ok: isNull("sfc")  [SmartFox client] -> "false"
[33.3s] CALL ok: catchPackets() [cross-domain listener] -> undefined
[33.3s] RESULT: callbacks=28/28 requestLoadGame=true
```

Each line is load-bearing:

| Check | Proves |
| :--- | :--- |
| `Externalizer::init done.` + `requestLoadGame()` | AS3 → JS direction of the bridge works |
| `callbacks=28/28` | All `ExternalInterface.addCallback` registrations in `Externalizer.as` land as callable host methods |
| `isTrue()` / `loadClient()` | JS → AS3 direction works |
| `params.sURL` non-null | The **live AQW client SWF loaded**, and `skua.swf`'s `onComplete` ran — `params.*` is only set there |
| `isNull("sfc")` → `false` | Cross-SWF reflection reaches the game's **SmartFoxServer client object** |
| `catchPackets()` no error | A listener attached to `game.sfc` — **packet interception works** |

## Why Electron and not plain Chrome

Two host requirements, both satisfied here and neither by Ruffle desktop:

1. **ExternalInterface.** Ruffle desktop registers no provider, so
   `skua.swf` dies immediately with
   `Error #2067: The ExternalInterface is not available in this container`.
   The web build bridges it to JS. The API itself is fully implemented in
   `ruffle_core` either way.
2. **CORS.** `game.aq.com` serves `/game/gamefiles/*.swf` with
   `Access-Control-Allow-Origin: *`, but `/game/api/data/gameversion` and
   `/game/api/login/now` send **no such header**, so a browser blocks them and
   the client never learns which SWF to load. `main.js` fixes this properly
   with `webRequest.onHeadersReceived` — not `--disable-web-security`.

## Known noise in the report

- **~2850 × `Error #1009 ... (accessing field: ModalStack)`** — this is a real
  bug in `Skua.AS3/skua/src/skua/module/QuestRequirementWiki.as:18`, not a
  Ruffle problem. It reads `game.ui.ModalStack` and null-guards the result but
  not `game.ui`, which is null until login. `Modules.handleFrame` is on
  `ENTER_FRAME` from game load onward, so it throws ~30×/sec pre-login under
  real Flash too — Flash just swallows uncaught errors without a debugger
  attached. Fix: `if (!game.ui) return;`
- **`Unknown device font ...`** — cosmetic, Ruffle lacks the host fonts.
- **Three AVM2 stubs total:** `Security.allowDomain()` (no-op, harmless here),
  `Dictionary` with weak keys, and `Loader.load() addChild at the correct time`.

## What this does NOT prove

- **Login and gameplay.** The harness never authenticates. Someone else's
  [aquastar-ruffle](https://github.com/aquaspy/aquastar-ruffle) launcher does
  demonstrate full login, server switching and play on Ruffle.
- **Sockets.** Browser-hosted Ruffle needs a WebSocket→TCP proxy for the
  SmartFox connection (`socketProxy` config). Ruffle **desktop** has native TCP
  (`--tcp-connections allow`), so a custom Rust host would not need the bridge.
- **Long-session stability.** Artix cite a Ruffle memory leak as the blocker for
  their bigger games, and aquastar reports poor FPS in crowded rooms. Both are
  directly relevant to multi-day farming and are Ruffle-side, not fixable here.

## Files

| File | Purpose |
| :--- | :--- |
| `main.js` | Electron host: static server, CORS header injection, report capture |
| `public/index.html` | The harness — drives the bridge and logs every result |
| `setup.js` | Downloads Ruffle web (pinned `v0.6.0`), stages `skua.swf` |
| `serve.py` | Standalone server, for running the harness in plain headless Chrome |

`public/ruffle/`, `public/skua.swf`, `node_modules/` and `report.txt` are
generated and gitignored.

## Headless (Linux, CI, Docker)

`npm run test:headless` runs the identical harness under Puppeteer instead of
Electron — no desktop, no Windows. It fixes CORS the same production way, via
CDP `Fetch` interception scoped to `https://*.aq.com/game/api/*` (the SWF assets
already send `ACAO: *`, so megabytes never pass through the debugger).

```bash
npm install && npm run setup
npm run test:headless        # exits 0 on PASS, 1 on FAIL
```

Verified on Ubuntu 20.04 (WSL2) and Windows. Typical headless run:

```
[runner] done. CORS headers injected on 1 responses.
[13.3s] RESULT: GAME SWF LOADED - cross-SWF reflection works
[13.3s] RESULT: callbacks=28/28 requestLoadGame=true
=== PASS ===
```

### In Docker

```bash
docker compose run --rm ruffle-test
# with credentials, supplied at run time only:
docker compose run --rm -e AQW_USER=alt -e AQW_PASS=... ruffle-test
```

`docker/Dockerfile.ruffle-test` is the one container in this repo that *runs*
something rather than building it. `credentials.local.json` and `node_modules`
are excluded from the build context — never bake a credential into a layer.

> Not yet verified end to end: Docker was not installed on the machine where
> this was written. The harness itself is verified on Linux and Windows; the
> unproven part is only the Debian package list in the Dockerfile.
