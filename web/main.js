// Electron host for the AQW client on Ruffle.
//
// This is the "app mode" counterpart to docs/ruffle-test/main.js: it opens a
// visible window and stays open, rather than running the bridge harness and
// quitting. Inside the KasmVNC container that window is what you see at :3000.
//
// Three jobs:
//   1. serve web/public over 127.0.0.1
//   2. add Access-Control-Allow-Origin to *.aq.com responses, which
//      /game/api/* omits (gameversion, login and the server list all need it)
//   3. run the WebSocket<->TCP bridge so the SWF can reach SmartFoxServer

const { app, BrowserWindow, session } = require('electron');
const http = require('http');
const https = require('https');
const net = require('net');
const { spawn } = require('child_process');
const readline = require('readline');
const fs = require('fs');
const path = require('path');
const socketProxy = require('./socket-proxy');

const ROOT = __dirname;
const PUBLIC = path.join(ROOT, 'public');
const PORT = Number(process.env.PORT || 8770);
const PROXY_PORT = Number(process.env.SOCKET_PROXY_PORT || 8181);
const SERVERS_API = 'https://game.aq.com/game/api/data/servers';

// Skua modules to switch off once the game loads.
//
// QuestRequirementWiki.as:18 and QuestItemRates.as:12 both read
// game.ui.ModalStack and guard the result but not game.ui, which is null
// whenever no UI is up. Both are enabled by default and run on ENTER_FRAME, so
// each throws an uncaught AVM2 error ~30 times a second, every one formatted
// with a stack trace and marshalled over IPC. OptimizePlayers guards the same
// access correctly (`if (game.ui != null)`) and is fine.
//
// Set DISABLE_MODULES="" to keep them all.
const DISABLE_MODULES = (process.env.DISABLE_MODULES ?? 'QuestRequirementWiki,QuestItemRates')
  .split(',').map(s => s.trim()).filter(Boolean);

// Skua modules to switch ON. These are its own performance modules, all
// disabled by default: DisableFX and HidePlayers cut how much of the Flash
// display tree has to be walked and drawn each frame, which is the single
// biggest lever inside the game. Off by default here because they change what
// you see.
const ENABLE_MODULES = (process.env.ENABLE_MODULES ?? '')
  .split(',').map(s => s.trim()).filter(Boolean);

// Ruffle render quality. With no GPU this is software rasterisation, and
// anti-aliasing is charged straight to the CPU. 'low' disables AA and bitmap
// smoothing. Raise to 'medium'/'high' if you have cycles or a real GPU.
const RUFFLE_QUALITY = process.env.RUFFLE_QUALITY || 'low';

// Rendering controls. These need the patched Ruffle (branch aqw-loader-fixes);
// the official release ignores them. All three can be changed live from the
// control bar under the game, or per-load with ?renderer=&scale=&fps= in the
// page URL.
//
// Tested in a logged-in Battleon: wgpu-webgl loads fully at both Max and a
// 15 fps cap. webgl works with Ruffle 560f6f6f3 and is much lighter, but
// anything AQW draws into a BitmapData stays blank (cooldown overlays; map
// backgrounds are worked around in index.html), so it stays opt-in.
//
// RUFFLE_RENDERER: wgpu-webgl (full visuals) or webgl (lighter, no filters,
// less complete).
const RUFFLE_RENDERER = process.env.RUFFLE_RENDERER || 'wgpu-webgl';
// Fraction of display resolution to render at; the browser upscales.
const RENDER_SCALE = Number(process.env.RENDER_SCALE || '1');
// Most renders per second. 0 = headless (nothing drawn), Infinity = unlimited.
const MAX_RENDER_FPS = Number(process.env.MAX_RENDER_FPS || 'Infinity');

// Chrome DevTools Protocol, for driving the game remotely (screenshots, input,
// calling the Skua bridge from Puppeteer). Off unless REMOTE_DEBUG_PORT is set.
//
// There is NO authentication: anyone who can reach the port controls the
// session, logged-in account included. Publish it on a LAN address only, never
// through a reverse proxy or to the internet.
//
// Electron binds the debugger to 127.0.0.1 only, so it listens on an internal
// port and a plain TCP relay republishes it on REMOTE_DEBUG_PORT for Docker to
// map. Connect by IP address: Chrome rejects DevTools HTTP requests whose Host
// header is a hostname other than localhost.
// Tolerates stray quotes/spaces: in compose's list form, `- VAR= "9222"` passes
// the value as ` "9222"` verbatim.
const REMOTE_DEBUG_RAW = (process.env.REMOTE_DEBUG_PORT || '').replace(/["'\s]/g, '');
const REMOTE_DEBUG_PORT = Number(REMOTE_DEBUG_RAW) || 0;
const INTERNAL_DEBUG_PORT = 19222;
if (REMOTE_DEBUG_RAW && !REMOTE_DEBUG_PORT) {
  console.error(`[host] REMOTE_DEBUG_PORT=${JSON.stringify(process.env.REMOTE_DEBUG_PORT)} is not a port number; DevTools stays off`);
}
if (REMOTE_DEBUG_PORT) {
  app.commandLine.appendSwitch('remote-debugging-port', String(INTERNAL_DEBUG_PORT));
}

// Auto-login and recycling (all optional).
//
// AQW_USER / AQW_PASS: log in automatically, the way Skua does, and again
// after a disconnect or a recycle. AQW_SERVER picks the server by name (first
// online one otherwise). Pass the password as a secret; it is only ever served
// to this page (see /autologin below), never logged.
//
// RECYCLE_AFTER_MINUTES / RECYCLE_AFTER_MAP_CHANGES: reload the game client
// once either is reached, at a moment the character is not in combat, then log
// back in and return to the same map and cell. Ruffle keeps every SWF it ever
// loads for the whole session (maps and each player's gear, ~45 MB per room
// change), so a long session only gets bigger and slower; a reload resets it.
const AQW_USER = process.env.AQW_USER || '';
const AQW_PASS = process.env.AQW_PASS || '';
const AQW_SERVER = process.env.AQW_SERVER || '';
const RECYCLE_AFTER_MINUTES = Number(process.env.RECYCLE_AFTER_MINUTES || 0) || 0;
const RECYCLE_AFTER_MAP_CHANGES = Number(process.env.RECYCLE_AFTER_MAP_CHANGES || 0) || 0;

// Skua itself (Skua.App.Avalonia: scripts, combat, quests, options, and the
// VibeSkua windows on this desktop) next to the page, driving skua.swf
// through the bridge in public/skua-bridge.js. Off unless SKUA_HOST=1;
// SKUA_UI=0 runs it without windows. Restarted if it exits.
//
// Its control API (start/stop scripts, status, logs) has NO authentication:
// whatever reaches it can run arbitrary code as the bot. It binds to
// 127.0.0.1 by default (use `docker exec`); SKUA_API_PREFIX=http://+:8791/
// opens it to wherever the port is published - a LAN address only.
const unquote = v => (v || '').trim().replace(/^["']|["']$/g, '').trim();
const SKUA_HOST = /^(1|true|yes)$/i.test(unquote(process.env.SKUA_HOST));
const SKUA_HOST_BIN = unquote(process.env.SKUA_HOST_BIN) || '/opt/skua/Skua.App.Avalonia';
const SKUA_UI = !/^(0|false|no)$/i.test(unquote(process.env.SKUA_UI));
const SKUA_BRIDGE_PREFIX = unquote(process.env.SKUA_BRIDGE_PREFIX) || 'http://127.0.0.1:8790/';
const SKUA_BRIDGE_URL = SKUA_BRIDGE_PREFIX.replace(/^http/, 'ws').replace('://+:', '://127.0.0.1:').replace('://*:', '://127.0.0.1:');

let skuaChild = null;

function startSkuaHost() {
  if (!fs.existsSync(SKUA_HOST_BIN)) {
    console.error(`[host] SKUA_HOST=1 but ${SKUA_HOST_BIN} does not exist; Skua stays off`);
    return;
  }
  let delay = 2000;
  const run = () => {
    const started = Date.now();
    const child = spawn(SKUA_HOST_BIN, SKUA_UI ? [] : ['--headless'], {
      env: {
        ...process.env,
        SKUA_BRIDGE_PREFIX,
        SKUA_BRIDGE_ORIGINS: unquote(process.env.SKUA_BRIDGE_ORIGINS) || `http://127.0.0.1:${PORT}`,
        DOTNET_CLI_TELEMETRY_OPTOUT: '1',
      },
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    skuaChild = child;
    readline.createInterface({ input: child.stdout }).on('line', l => console.log(`[skua] ${l.slice(0, 500)}`));
    readline.createInterface({ input: child.stderr }).on('line', l => console.error(`[skua] ${l.slice(0, 500)}`));
    child.on('error', e => console.error(`[host] Skua.Host: ${e.message}`));
    child.on('exit', (code, signal) => {
      skuaChild = null;
      // Back off only if it keeps dying young.
      delay = Date.now() - started > 60000 ? 2000 : Math.min(delay * 2, 60000);
      console.error(`[host] Skua.Host exited (${signal || code}); restarting in ${delay / 1000}s`);
      setTimeout(run, delay);
    });
  };
  app.on('will-quit', () => skuaChild?.kill());
  run();
}

function startDebugRelay() {
  net.createServer(client => {
    const upstream = net.connect(INTERNAL_DEBUG_PORT, '127.0.0.1');
    client.pipe(upstream).pipe(client);
    client.on('error', () => upstream.destroy());
    upstream.on('error', () => client.destroy());
  }).listen(REMOTE_DEBUG_PORT, '0.0.0.0', () => {
    console.log(`[host] DevTools protocol on :${REMOTE_DEBUG_PORT} (no auth - LAN only)`);
  });
}

const MIME = {
  '.html': 'text/html', '.js': 'text/javascript', '.wasm': 'application/wasm',
  '.swf': 'application/x-shockwave-flash', '.json': 'application/json',
  '.map': 'application/json', '.css': 'text/css',
};

// Hosts seen in the live server list. The proxy consults this on top of its
// static allowlist, so Artix adding a domain does not require a code change.
const discoveredHosts = new Set();

// Fetched server-side so the page never has to fight CORS for this one call.
function fetchServers() {
  return new Promise(resolve => {
    https.get(SERVERS_API, { headers: { 'User-Agent': 'vibeskua-web' } }, res => {
      let body = '';
      res.on('data', c => body += c);
      res.on('end', () => {
        try {
          const list = JSON.parse(body);
          const servers = Array.isArray(list) ? list : [];
          for (const srv of servers) {
            if (typeof srv.sIP === 'string' && srv.sIP) discoveredHosts.add(srv.sIP.toLowerCase());
          }
          resolve(servers);
        } catch { resolve([]); }
      });
    }).on('error', () => resolve([]));
  });
}

function serve() {
  return new Promise(resolve => {
    http.createServer(async (req, res) => {
      if (req.url === '/servers') {
        const servers = await fetchServers();
        res.writeHead(200, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({
          servers, proxyPort: PROXY_PORT,
          disableModules: DISABLE_MODULES,
          enableModules: ENABLE_MODULES,
          quality: RUFFLE_QUALITY,
          renderer: RUFFLE_RENDERER,
          renderScale: RENDER_SCALE,
          // JSON has no Infinity; null means unlimited.
          maxRenderFps: Number.isFinite(MAX_RENDER_FPS) ? MAX_RENDER_FPS : null,
          autoLogin: Boolean(AQW_USER && AQW_PASS),
          recycleAfterMinutes: RECYCLE_AFTER_MINUTES,
          recycleAfterMapChanges: RECYCLE_AFTER_MAP_CHANGES,
          skuaBridgeUrl: SKUA_HOST ? SKUA_BRIDGE_URL : null,
        }));
        return;
      }
      // Credentials for auto-login, for this page only. Other pages loaded in
      // the browser could request 127.0.0.1 too, so: the Host must be ours
      // (defeats DNS rebinding), and the custom header forces a CORS preflight
      // on any cross-origin request, which this server never approves.
      if (req.url === '/autologin') {
        const own = req.headers.host === `127.0.0.1:${PORT}` && req.headers['x-vibeskua'] === '1';
        if (!own || !AQW_USER || !AQW_PASS) { res.writeHead(404); res.end(); return; }
        res.writeHead(200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
        res.end(JSON.stringify({ user: AQW_USER, pass: AQW_PASS, server: AQW_SERVER }));
        return;
      }
      const rel = decodeURIComponent(req.url.split('?')[0]).replace(/^\/+/, '') || 'index.html';
      const file = path.join(PUBLIC, rel);
      if (!file.startsWith(PUBLIC) || !fs.existsSync(file)) { res.writeHead(404); res.end(); return; }
      res.writeHead(200, { 'Content-Type': MIME[path.extname(file)] || 'application/octet-stream' });
      fs.createReadStream(file).pipe(res);
    }).listen(PORT, '127.0.0.1', resolve);
  });
}

function installCorsFix() {
  session.defaultSession.webRequest.onHeadersReceived(
    { urls: ['https://*.aq.com/*'] },
    (details, cb) => {
      const h = details.responseHeaders || {};
      for (const k of Object.keys(h)) {
        if (k.toLowerCase() === 'access-control-allow-origin') delete h[k];
      }
      h['Access-Control-Allow-Origin'] = ['*'];
      cb({ responseHeaders: h });
    }
  );
}

app.whenReady().then(async () => {
  if (!fs.existsSync(path.join(PUBLIC, 'skua.swf')) ||
      !fs.existsSync(path.join(PUBLIC, 'ruffle', 'ruffle.js'))) {
    console.error('[host] public/ is not staged - run `npm run setup` first');
    app.exit(1);
    return;
  }

  await serve();
  installCorsFix();
  // Prime the host set before the page asks, so the first connection attempt
  // is not refused in a race with the page's own /servers fetch.
  await fetchServers();
  socketProxy.start({
    port: PROXY_PORT,
    log: m => console.log(m),
    allow: host => discoveredHosts.has(host.toLowerCase()),
  });
  console.log(`[host] serving http://127.0.0.1:${PORT}`);
  if (REMOTE_DEBUG_PORT) startDebugRelay();
  if (SKUA_HOST) startSkuaHost();

  const win = new BrowserWindow({
    width: 1000,
    height: 640,
    autoHideMenuBar: true,
    backgroundColor: '#111111',
    webPreferences: { nodeIntegration: false, contextIsolation: true },
  });
  // Collapse repeats. Ruffle can emit the same error every frame, and writing
  // each one to stdout is itself a meaningful cost once Docker is capturing it.
  let lastMsg = null, repeats = 0, flushTimer = null;
  const flush = () => {
    if (repeats > 0) console.log(`[page] (previous line repeated ${repeats}x)`);
    repeats = 0;
    flushTimer = null;
  };
  win.webContents.on('console-message', (_e, _lvl, msg) => {
    const line = msg.slice(0, 300);
    if (line === lastMsg) {
      repeats++;
      if (!flushTimer) flushTimer = setTimeout(flush, 10000);
      return;
    }
    flush();
    lastMsg = line;
    console.log('[page]', line);
  });
  win.loadURL(`http://127.0.0.1:${PORT}/index.html`);
});

// Deliberately does NOT quit on window close: in the container the window is
// the whole session, and s6 would just restart us.
app.on('window-all-closed', () => {});
