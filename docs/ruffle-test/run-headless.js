// Headless runner: same harness as main.js, but driven by Puppeteer instead of
// Electron. This is the shape a Linux container or CI job uses — no desktop,
// no Windows, no Flash.
//
// CORS is fixed the production way here too: game.aq.com serves /game/api/*
// without Access-Control-Allow-Origin, so we intercept just those (small JSON)
// responses over CDP and add the header. The SWF assets already send ACAO:* and
// are left to stream normally, so no megabytes pass through the debugger.

const puppeteer = require('puppeteer');
const http = require('http');
const fs = require('fs');
const path = require('path');

const ROOT = __dirname;
const PUBLIC = path.join(ROOT, 'public');
const PORT = Number(process.env.PORT || 8767);
const REPORT = path.join(ROOT, 'report.txt');
const RUN_SECONDS = Number(process.env.RUN_SECONDS || 90);

const MIME = {
  '.html': 'text/html', '.js': 'text/javascript', '.wasm': 'application/wasm',
  '.swf': 'application/x-shockwave-flash', '.json': 'application/json',
  '.map': 'application/json', '.css': 'text/css',
};

function loadCreds() {
  if (process.env.AQW_USER && process.env.AQW_PASS) {
    return { username: process.env.AQW_USER, password: process.env.AQW_PASS };
  }
  const f = path.join(ROOT, 'credentials.local.json');
  if (!fs.existsSync(f)) return null;
  try {
    const c = JSON.parse(fs.readFileSync(f, 'utf8'));
    return c.username && c.password ? c : null;
  } catch { return null; }
}
const CREDS = loadCreds();
const redact = s => (CREDS && s ? String(s).split(CREDS.password).join('***REDACTED***') : s);

function serve() {
  return new Promise(resolve => {
    http.createServer((req, res) => {
      if (req.method === 'POST' && req.url === '/report') {
        let body = '';
        req.on('data', c => body += c);
        req.on('end', () => {
          fs.writeFileSync(REPORT, redact(body), 'utf8');
          res.writeHead(204); res.end();
        });
        return;
      }
      if (req.url === '/creds') {
        res.writeHead(200, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify(CREDS || null));
        return;
      }
      const rel = decodeURIComponent(req.url.split('?')[0]).replace(/^\/+/, '') || 'index.html';
      const file = path.join(PUBLIC, rel);
      if (!file.startsWith(PUBLIC) || !fs.existsSync(file)) { res.writeHead(404); res.end(); return; }
      res.writeHead(200, { 'Content-Type': MIME[path.extname(file)] || 'application/octet-stream' });
      fs.createReadStream(file).pipe(res);
    }).listen(PORT, '127.0.0.1', () => resolve());
  });
}

async function installCorsFix(page) {
  const client = await page.createCDPSession();
  await client.send('Fetch.enable', {
    patterns: [{ urlPattern: 'https://*.aq.com/game/api/*', requestStage: 'Response' }],
  });
  let patched = 0;
  client.on('Fetch.requestPaused', async e => {
    try {
      const headers = (e.responseHeaders || [])
        .filter(h => h.name.toLowerCase() !== 'access-control-allow-origin')
        .concat([{ name: 'Access-Control-Allow-Origin', value: '*' }]);
      const body = await client.send('Fetch.getResponseBody', { requestId: e.requestId });
      await client.send('Fetch.fulfillRequest', {
        requestId: e.requestId,
        responseCode: e.responseStatusCode || 200,
        responseHeaders: headers,
        body: body.base64Encoded ? body.body : Buffer.from(body.body).toString('base64'),
      });
      patched++;
    } catch {
      try { await client.send('Fetch.continueRequest', { requestId: e.requestId }); } catch {}
    }
  });
  return () => patched;
}

(async () => {
  if (fs.existsSync(REPORT)) fs.unlinkSync(REPORT);
  if (!fs.existsSync(path.join(PUBLIC, 'skua.swf')) ||
      !fs.existsSync(path.join(PUBLIC, 'ruffle', 'ruffle.js'))) {
    console.error('[runner] public/ is not staged — run `npm run setup` first');
    process.exit(1);
  }

  await serve();
  console.log(`[runner] serving http://127.0.0.1:${PORT}`);
  console.log(CREDS
    ? `[runner] credentials loaded for "${CREDS.username}" - login step ENABLED`
    : '[runner] no credentials found - login step SKIPPED');

  const browser = await puppeteer.launch({
    headless: true,
    args: [
      '--no-sandbox', '--disable-setuid-sandbox', '--disable-dev-shm-usage',
      '--enable-unsafe-swiftshader', '--autoplay-policy=no-user-gesture-required',
    ],
  });

  const page = await browser.newPage();
  const patchedCount = await installCorsFix(page);
  page.on('console', m => {
    const t = m.text();
    if (/RESULT|CALL ok|CALL FAIL|LOGIN|AS3->JS/.test(t)) console.log('[page]', redact(t).slice(0, 200));
  });
  page.on('pageerror', e => console.log('[pageerror]', redact(String(e)).slice(0, 200)));

  await page.goto(`http://127.0.0.1:${PORT}/index.html`, { waitUntil: 'domcontentloaded' });
  await new Promise(r => setTimeout(r, RUN_SECONDS * 1000));
  await browser.close();

  console.log(`[runner] done. CORS headers injected on ${patchedCount()} responses.`);
  if (!fs.existsSync(REPORT)) { console.error('[runner] NO REPORT'); process.exit(1); }

  const report = fs.readFileSync(REPORT, 'utf8');
  console.log('=== summary ===');
  for (const line of report.split('\n')) {
    if (/RESULT|CALL ok|CALL FAIL|LOGIN|AS3->JS/.test(line)) console.log(line);
  }
  const ok = /callbacks=28\/28/.test(report) && /GAME SWF LOADED/.test(report);
  console.log(ok ? '=== PASS ===' : '=== FAIL ===');
  process.exit(ok ? 0 : 1);
})();
