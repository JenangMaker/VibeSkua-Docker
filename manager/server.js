'use strict';
// VibeSkua Manager: a web page to watch and control one VibeSkua instance
// from another machine, as Skua Manager does on Windows.
//
// It talks to VibeSkua's tab host API (Skua.App.Avalonia/TabHostWindow.Api.cs)
// server-side, with the API token, so neither the token nor the API is ever
// exposed to the browser. The browser gets this server's own login.
//
// No dependencies: Node's own http, crypto and fetch (Node 22+).
//
// Environment:
//   MANAGER_PASSWORD       required: the login password
//   MANAGER_USER           login name (admin)
//   VIBESKUA_URL           the tab host API (http://127.0.0.1:8789)
//   VIBESKUA_TOKEN         its SKUA_API_TOKEN, if set there
//   PORT                   where this listens (3040)
//   MANAGER_ALLOW          who may connect: "lan" (private addresses, the
//                          default), "any", or a comma list of addresses and
//                          IPv4/IPv6 ranges ("lan" may be one of them)
//   MANAGER_TRUST_PROXY    1: behind a reverse proxy; take the client address
//                          and https from X-Forwarded-For / -Proto
//   MANAGER_SECURE_COOKIE  1: mark the session cookie Secure (served over https)
//   MANAGER_SESSION_HOURS  how long a login lasts (12)

const http = require('http');
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const net = require('net');

const env = name => (process.env[name] || '').trim().replace(/^["']|["']$/g, '').trim();
const on = name => /^(1|true|yes|on)$/i.test(env(name));

const PORT = Number(env('PORT')) || 3040;
const TARGET = (env('VIBESKUA_URL') || 'http://127.0.0.1:8789').replace(/\/+$/, '');
const TOKEN = env('VIBESKUA_TOKEN');
const USER = env('MANAGER_USER') || 'admin';
const PASSWORD = process.env.MANAGER_PASSWORD || '';   // as given: quotes may be part of it
const SESSION_MS = (Number(env('MANAGER_SESSION_HOURS')) || 12) * 3600_000;
const TRUST_PROXY = on('MANAGER_TRUST_PROXY');
const SECURE_COOKIE = on('MANAGER_SECURE_COOKIE');
const PUBLIC = path.join(__dirname, 'public');
const COOKIE = 'vsm_session';
const MAX_BODY = 1 << 20;

if (!PASSWORD) {
  console.error('[manager] MANAGER_PASSWORD is not set; refusing to start without a login.');
  process.exit(1);
}
if (PASSWORD.length < 10) console.warn('[manager] MANAGER_PASSWORD is short; use 10 characters or more.');

// ---- who may connect --------------------------------------------------------

function allowList(spec) {
  if (spec.toLowerCase() === 'any') return null;
  const list = new net.BlockList();
  for (const raw of spec.split(',').map(s => s.trim()).filter(Boolean)) {
    if (raw.toLowerCase() === 'lan') {
      // Private, loopback and link-local ranges, and 100.64/10 (Tailscale
      // and other carrier-grade NAT overlays).
      for (const [a, p] of [['10.0.0.0', 8], ['172.16.0.0', 12], ['192.168.0.0', 16], ['127.0.0.0', 8],
        ['169.254.0.0', 16], ['100.64.0.0', 10]]) list.addSubnet(a, p, 'ipv4');
      for (const [a, p] of [['fc00::', 7], ['fe80::', 10]]) list.addSubnet(a, p, 'ipv6');
      list.addAddress('::1', 'ipv6');
      continue;
    }
    const [addr, prefix] = raw.split('/');
    const type = net.isIPv6(addr) ? 'ipv6' : 'ipv4';
    if (!net.isIP(addr)) { console.error(`[manager] MANAGER_ALLOW: ignoring "${raw}"`); continue; }
    if (prefix === undefined) list.addAddress(addr, type);
    else list.addSubnet(addr, Number(prefix), type);
  }
  return list;
}
const ALLOW = allowList(env('MANAGER_ALLOW') || 'lan');

function clientIp(req) {
  let ip = req.socket.remoteAddress || '';
  if (TRUST_PROXY && req.headers['x-forwarded-for']) ip = String(req.headers['x-forwarded-for']).split(',')[0].trim();
  return ip.replace(/^::ffff:/, '');
}

function allowed(ip) {
  if (!ALLOW) return true;
  try { return ALLOW.check(ip, net.isIPv6(ip) ? 'ipv6' : 'ipv4'); } catch { return false; }
}

// ---- login and sessions -----------------------------------------------------

const sessions = new Map();   // id -> { user, expires }
const failures = new Map();   // ip -> { count, first, lockedUntil }
const FAIL_LIMIT = 5, FAIL_WINDOW = 15 * 60_000;

setInterval(() => {
  const now = Date.now();
  for (const [id, s] of sessions) if (s.expires < now) sessions.delete(id);
  for (const [ip, f] of failures) if (f.first + FAIL_WINDOW < now && f.lockedUntil < now) failures.delete(ip);
}, 60_000).unref();

const digest = s => crypto.createHash('sha256').update(String(s)).digest();
const same = (a, b) => crypto.timingSafeEqual(digest(a), digest(b));

function cookies(req) {
  const out = {};
  for (const part of String(req.headers.cookie || '').split(';')) {
    const i = part.indexOf('=');
    if (i > 0) out[part.slice(0, i).trim()] = part.slice(i + 1).trim();
  }
  return out;
}

function session(req) {
  const id = cookies(req)[COOKIE];
  const s = id && sessions.get(id);
  if (!s || s.expires < Date.now()) return null;
  return s;
}

function secure(req) {
  return SECURE_COOKIE || (TRUST_PROXY && req.headers['x-forwarded-proto'] === 'https');
}

function setCookie(req, res, value, maxAgeMs) {
  res.setHeader('Set-Cookie', `${COOKIE}=${value}; Path=/; HttpOnly; SameSite=Strict; Max-Age=${Math.floor(maxAgeMs / 1000)}${secure(req) ? '; Secure' : ''}`);
}

async function login(req, res, ip) {
  const f = failures.get(ip);
  if (f && f.lockedUntil > Date.now()) {
    return send(res, 429, { error: `Too many failed logins. Try again in ${Math.ceil((f.lockedUntil - Date.now()) / 60000)} min.` });
  }
  let body;
  try { body = JSON.parse((await readBody(req)).toString('utf8') || '{}'); } catch { return send(res, 400, { error: 'bad request' }); }
  // Both compared every time, so the timing says nothing about which was wrong.
  const userOk = same(body.user || '', USER);
  const passwordOk = same(body.password || '', PASSWORD);
  if (!(userOk && passwordOk)) {
    const now = Date.now();
    const entry = f && f.first + FAIL_WINDOW > now ? f : { count: 0, first: now, lockedUntil: 0 };
    entry.count++;
    if (entry.count >= FAIL_LIMIT) entry.lockedUntil = now + FAIL_WINDOW;
    failures.set(ip, entry);
    console.warn(`[manager] failed login from ${ip} (${entry.count})`);
    await new Promise(r => setTimeout(r, 600));
    return send(res, 401, { error: 'Wrong user name or password.' });
  }
  failures.delete(ip);
  const id = crypto.randomBytes(32).toString('base64url');
  sessions.set(id, { user: USER, expires: Date.now() + SESSION_MS });
  setCookie(req, res, id, SESSION_MS);
  console.log(`[manager] ${USER} logged in from ${ip}`);
  send(res, 200, { user: USER });
}

function logout(req, res) {
  const id = cookies(req)[COOKIE];
  if (id) sessions.delete(id);
  setCookie(req, res, '', 0);
  send(res, 200, { ok: true });
}

// ---- the VibeSkua API, relayed ----------------------------------------------

async function relay(req, res, apiPath, query, s, ip) {
  const method = req.method;
  const body = method === 'GET' || method === 'HEAD' ? undefined : await readBody(req);
  const headers = { 'Content-Type': req.headers['content-type'] || 'application/json' };
  if (TOKEN) headers.Authorization = `Bearer ${TOKEN}`;
  // What was done, by whom; never the body (account passwords travel in it).
  if (method !== 'GET') console.log(`[manager] ${s.user}@${ip}: ${method} ${apiPath}${query}`);
  try {
    const reply = await fetch(TARGET + apiPath + query, {
      method, headers,
      // Even when empty: the tab host refuses a POST without a length (411).
      body,
      signal: AbortSignal.timeout(120_000),
    });
    const text = await reply.text();
    // Not this page's login: the browser must not take it for one.
    if (reply.status === 401) {
      return send(res, 502, { error: 'VibeSkua refused the API token: check VIBESKUA_TOKEN against its SKUA_API_TOKEN' });
    }
    res.writeHead(reply.status, { 'Content-Type': reply.headers.get('content-type') || 'application/json', 'Cache-Control': 'no-store' });
    res.end(text);
  } catch (e) {
    send(res, 502, { error: `VibeSkua did not answer (${TARGET}): ${e.cause?.code || e.message}` });
  }
}

// ---- serving ----------------------------------------------------------------

const MIME = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8', '.svg': 'image/svg+xml', '.ico': 'image/x-icon' };

const SECURITY_HEADERS = {
  'Content-Security-Policy': "default-src 'self'; img-src 'self' data:; script-src 'self'; style-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'",
  'X-Content-Type-Options': 'nosniff',
  'Referrer-Policy': 'no-referrer',
  'X-Frame-Options': 'DENY',
};

function send(res, status, obj) {
  if (res.headersSent) return res.end();
  res.writeHead(status, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
  res.end(JSON.stringify(obj));
}

function readBody(req) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    let size = 0;
    req.on('data', c => {
      size += c.length;
      if (size > MAX_BODY) { reject(new Error('body too large')); req.destroy(); return; }
      chunks.push(c);
    });
    req.on('end', () => resolve(Buffer.concat(chunks)));
    req.on('error', reject);
  });
}

function serveFile(res, rel) {
  const file = path.join(PUBLIC, rel);
  if (!file.startsWith(PUBLIC + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) {
    res.writeHead(404); res.end(); return;
  }
  res.writeHead(200, { 'Content-Type': MIME[path.extname(file)] || 'application/octet-stream', 'Cache-Control': 'no-cache' });
  fs.createReadStream(file).pipe(res);
}

const server = http.createServer(async (req, res) => {
  const ip = clientIp(req);
  for (const [k, v] of Object.entries(SECURITY_HEADERS)) res.setHeader(k, v);
  if (!allowed(ip)) {
    console.warn(`[manager] refused ${ip} (MANAGER_ALLOW)`);
    res.writeHead(403, { 'Content-Type': 'text/plain' });
    res.end('Not allowed from this address. See MANAGER_ALLOW.');
    return;
  }
  let url;
  try { url = new URL(req.url, 'http://manager'); } catch { res.writeHead(400); res.end(); return; }
  const p = url.pathname;
  try {
    if (p.startsWith('/api/') || p === '/login' || p === '/logout') {
      // Every call comes from this page's script with this header: a form
      // or link on another site cannot add it, and a cross-site script would
      // need a CORS preflight, which this server never answers.
      if (req.headers['x-manager'] !== '1') return send(res, 403, { error: 'missing X-Manager header' });
      if (p === '/login' && req.method === 'POST') return await login(req, res, ip);
      if (p === '/logout' && req.method === 'POST') return logout(req, res);
      const s = session(req);
      if (!s) return send(res, 401, { error: 'not logged in' });
      if (p === '/api/session') return send(res, 200, { user: s.user, target: TARGET });
      return await relay(req, res, p.slice('/api'.length), url.search, s, ip);
    }
    if (req.method !== 'GET' && req.method !== 'HEAD') { res.writeHead(405); res.end(); return; }
    serveFile(res, p === '/' ? 'index.html' : decodeURIComponent(p).replace(/^\/+/, ''));
  } catch (e) {
    console.error(`[manager] ${req.method} ${p}: ${e.message}`);
    send(res, 500, { error: 'internal error' });
  }
});

server.listen(PORT, () => {
  console.log(`[manager] listening on :${PORT}, managing ${TARGET}${TOKEN ? ' (with token)' : ''}; allow: ${env('MANAGER_ALLOW') || 'lan'}`);
});

for (const sig of ['SIGTERM', 'SIGINT']) {
  process.on(sig, () => {
    server.close(() => process.exit(0));
    setTimeout(() => process.exit(0), 2000).unref();
  });
}
