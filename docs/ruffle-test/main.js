// Electron host for the Skua/Ruffle bridge test.
//
// Proves the production-shape fix for the one real blocker found in the
// headless-Chrome run: game.aq.com serves /game/api/* WITHOUT an
// Access-Control-Allow-Origin header, so browser-hosted Ruffle cannot fetch
// gameversion or login. Here we inject the header via webRequest instead of
// disabling web security.

const { app, BrowserWindow, session } = require('electron');
const http = require('http');
const fs = require('fs');
const path = require('path');

const ROOT = __dirname;
const PORT = 8766;
const REPORT = path.join(ROOT, 'report.txt');
const CREDS_FILE = path.join(ROOT, 'credentials.local.json');

// Optional. Create credentials.local.json yourself: {"username":"...","password":"..."}
// It is gitignored, never logged, and never leaves this machine. Env vars work too.
// USE A THROWAWAY ACCOUNT — see README.
function loadCreds() {
  const env = process.env.AQW_USER && process.env.AQW_PASS
    ? { username: process.env.AQW_USER, password: process.env.AQW_PASS }
    : null;
  if (env) return env;
  if (!fs.existsSync(CREDS_FILE)) return null;
  try {
    const c = JSON.parse(fs.readFileSync(CREDS_FILE, 'utf8'));
    return c.username && c.password ? c : null;
  } catch (e) {
    console.error('[host] credentials.local.json is not valid JSON, ignoring');
    return null;
  }
}
const CREDS = loadCreds();

// Belt and braces: scrub the secret from anything on its way to disk or console.
function redact(s) {
  if (!CREDS || !s) return s;
  return String(s).split(CREDS.password).join('***REDACTED***');
}

const MIME = {
  '.html': 'text/html', '.js': 'text/javascript', '.wasm': 'application/wasm',
  '.swf': 'application/x-shockwave-flash', '.json': 'application/json',
  '.map': 'application/json', '.css': 'text/css',
};

function serve() {
  return new Promise(resolve => {
    http.createServer((req, res) => {
      if (req.method === 'POST' && req.url === '/report') {
        let body = '';
        req.on('data', c => body += c);
        req.on('end', () => {
          const clean = redact(body);
          fs.writeFileSync(REPORT, clean, 'utf8');
          console.log(`[host] report written (${clean.length} bytes)`);
          res.writeHead(204); res.end();
        });
        return;
      }
      // Served only over 127.0.0.1 to the harness page itself.
      if (req.url === '/creds') {
        res.writeHead(200, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify(CREDS || null));
        return;
      }
      const rel = decodeURIComponent(req.url.split('?')[0]).replace(/^\/+/, '') || 'index.html';
      const file = path.join(ROOT, 'public', rel);
      if (!file.startsWith(path.join(ROOT, 'public')) || !fs.existsSync(file)) {
        res.writeHead(404); res.end('not found'); return;
      }
      res.writeHead(200, { 'Content-Type': MIME[path.extname(file)] || 'application/octet-stream' });
      fs.createReadStream(file).pipe(res);
    }).listen(PORT, '127.0.0.1', () => {
      console.log(`[host] serving http://127.0.0.1:${PORT}`);
      resolve();
    });
  });
}

let injected = 0;
function installCorsFix() {
  session.defaultSession.webRequest.onHeadersReceived(
    { urls: ['https://game.aq.com/*', 'https://*.aq.com/*'] },
    (details, cb) => {
      const h = details.responseHeaders || {};
      // Strip any existing casing variant, then set a permissive one.
      for (const k of Object.keys(h)) {
        if (k.toLowerCase() === 'access-control-allow-origin') delete h[k];
      }
      h['Access-Control-Allow-Origin'] = ['*'];
      injected++;
      cb({ responseHeaders: h });
    }
  );
  console.log('[host] CORS header injection installed for *.aq.com');
}

app.whenReady().then(async () => {
  if (fs.existsSync(REPORT)) fs.unlinkSync(REPORT);
  await serve();
  installCorsFix();

  const win = new BrowserWindow({
    width: 1000, height: 700, show: process.env.SHOW_WINDOW === '1',
    webPreferences: { nodeIntegration: false, contextIsolation: true },
  });
  console.log(CREDS
    ? `[host] credentials loaded for "${CREDS.username}" - login step ENABLED`
    : '[host] no credentials found - login step SKIPPED (see README)');

  win.webContents.on('console-message', (_e, _lvl, msg) => {
    if (/ERROR|WARN|RESULT|CALL/.test(msg)) console.log('[page]', redact(msg).slice(0, 200));
  });
  win.loadURL(`http://127.0.0.1:${PORT}/index.html`);

  const deadline = Number(process.env.RUN_SECONDS || 90) * 1000;
  setTimeout(() => {
    console.log(`[host] done. CORS headers injected on ${injected} responses.`);
    if (fs.existsSync(REPORT)) console.log('[host] REPORT OK');
    else console.log('[host] NO REPORT');
    app.quit();
  }, deadline);
});

app.on('window-all-closed', () => app.quit());
