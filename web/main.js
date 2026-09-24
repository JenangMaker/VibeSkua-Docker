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
// the official release ignores them. Measured on 2 cores with no GPU, where
// rasterisation is nearly all of the CPU cost:
//
//   wgpu-webgl, scale 1.0, unlimited  -> 1.89 cores, ~15 ticks/s, 500ms+ stalls
//   webgl,      scale 0.75, 15 fps    -> 1.35 cores, full-speed game logic
//   webgl,      scale 0.5,  10 fps    -> 0.74 cores, full-speed game logic
//   render paused (0 fps)             -> 0.17 cores, full-speed game logic
//
// The game keeps running at full speed whatever these are set to; they only
// decide how much of it gets drawn. They can also be changed live from the
// control bar under the game.
//
// RUFFLE_RENDERER: webgl is much steadier than wgpu-webgl in software, but does
// not draw filters (GlowFilter, drop shadows). Use wgpu-webgl for full visuals.
const RUFFLE_RENDERER = process.env.RUFFLE_RENDERER || 'webgl';
// Fraction of display resolution to render at; the browser upscales.
// 0.5 halves CPU again but is noticeably blurry; 0.75 is the default.
const RENDER_SCALE = Number(process.env.RENDER_SCALE || '0.75');
// Most renders per second. 0 = headless (nothing drawn), Infinity = unlimited.
const MAX_RENDER_FPS = Number(process.env.MAX_RENDER_FPS ?? '15');

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
        }));
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
