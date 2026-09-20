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
        res.end(JSON.stringify({ servers, proxyPort: PROXY_PORT }));
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
  win.webContents.on('console-message', (_e, _lvl, msg) => console.log('[page]', msg.slice(0, 300)));
  win.loadURL(`http://127.0.0.1:${PORT}/index.html`);
});

// Deliberately does NOT quit on window close: in the container the window is
// the whole session, and s6 would just restart us.
app.on('window-all-closed', () => {});
