// Fetches the Ruffle web build and stages skua.swf next to the harness.
// Neither is committed: Ruffle is ~30MB, and skua.swf belongs to Skua.AS3.

const fs = require('fs');
const path = require('path');
const https = require('https');
const { execFileSync } = require('child_process');

const ROOT = __dirname;
const PUBLIC = path.join(ROOT, 'public');
const RUFFLE_DIR = path.join(PUBLIC, 'ruffle');
const SWF_SRC = path.join(ROOT, '..', 'Skua.AS3', 'skua', 'bin', 'skua.swf');
const SWF_DST = path.join(PUBLIC, 'skua.swf');

// Pin the version the findings were produced against; bump deliberately.
const RUFFLE_VERSION = process.env.RUFFLE_VERSION || 'v0.6.0';
const ZIP_NAME = `ruffle-${RUFFLE_VERSION.replace(/^v/, '')}-web-selfhosted.zip`;

// Use a custom Ruffle build instead of the official release -- e.g. one with
// the Loader.unloadAndStop / Loader.close fixes, which AQW hits on every room
// change and every time a player leaves.
//   RUFFLE_WEB_DIR  a local self-hosted build (web/packages/selfhosted/dist)
//   RUFFLE_WEB_URL  a zip of one, e.g. a Gitea release asset
const RUFFLE_WEB_DIR = process.env.RUFFLE_WEB_DIR || '';
const ZIP_URL = process.env.RUFFLE_WEB_URL
  || `https://github.com/ruffle-rs/ruffle/releases/download/${RUFFLE_VERSION}/${ZIP_NAME}`;

// Optional "user:token" for RUFFLE_WEB_URL on a private Gitea (repo release
// assets, or the generic package registry). Sent as HTTP Basic auth, and only
// to the host RUFFLE_WEB_URL names -- never to a redirect target elsewhere.
const RUFFLE_WEB_AUTH = (process.env.RUFFLE_WEB_AUTH || '').trim();

function get(url, dest, authHost = null) {
  return new Promise((resolve, reject) => {
    const headers = { 'User-Agent': 'vibeskua-ruffle-test' };
    const { host } = new URL(url);
    if (RUFFLE_WEB_AUTH && authHost && host === authHost) {
      headers.Authorization = 'Basic ' + Buffer.from(RUFFLE_WEB_AUTH).toString('base64');
    }
    https.get(url, { headers }, res => {
      if ([301, 302, 307, 308].includes(res.statusCode)) {
        res.resume();
        return resolve(get(new URL(res.headers.location, url).href, dest, authHost));
      }
      if ([401, 403, 404].includes(res.statusCode) && RUFFLE_WEB_AUTH === '' && process.env.RUFFLE_WEB_URL) {
        console.error(`HTTP ${res.statusCode}: a private Gitea answers 404 to anonymous requests.`);
        console.error('Provide RUFFLE_WEB_AUTH="user:token" (see web/README.md).');
      }
      if (res.statusCode !== 200) {
        res.resume();
        return reject(new Error(`${url} -> HTTP ${res.statusCode}`));
      }
      const f = fs.createWriteStream(dest);
      res.pipe(f);
      f.on('finish', () => f.close(resolve));
      f.on('error', reject);
    }).on('error', reject);
  });
}

(async () => {
  fs.mkdirSync(PUBLIC, { recursive: true });

  if (fs.existsSync(SWF_SRC)) {
    fs.copyFileSync(SWF_SRC, SWF_DST);
    console.log(`staged skua.swf (${fs.statSync(SWF_DST).size} bytes)`);
  } else if (fs.existsSync(SWF_DST)) {
    // Container builds copy skua.swf in directly; the repo tree isn't present.
    console.log('skua.swf already staged, leaving it alone');
  } else {
    console.error(`skua.swf not found at ${SWF_SRC} and none staged at ${SWF_DST}`);
    console.error('It is committed in the repo — check your clone.');
    process.exit(1);
  }

  if (RUFFLE_WEB_DIR) {
    if (!fs.existsSync(path.join(RUFFLE_WEB_DIR, 'ruffle.js'))) {
      console.error(`RUFFLE_WEB_DIR has no ruffle.js: ${RUFFLE_WEB_DIR}`);
      process.exit(1);
    }
    // Always refresh: the point of a local build is that it changes.
    fs.rmSync(RUFFLE_DIR, { recursive: true, force: true });
    fs.cpSync(RUFFLE_WEB_DIR, RUFFLE_DIR, { recursive: true });
    console.log(`ruffle web build copied from ${RUFFLE_WEB_DIR}`);
    return;
  }

  if (fs.existsSync(path.join(RUFFLE_DIR, 'ruffle.js'))) {
    console.log('ruffle web build already present, skipping download');
    return;
  }

  const zip = path.join(ROOT, ZIP_NAME);
  console.log(`downloading ${ZIP_URL}`);
  await get(ZIP_URL, zip, new URL(ZIP_URL).host);
  console.log(`downloaded ${fs.statSync(zip).size} bytes, extracting`);

  fs.mkdirSync(RUFFLE_DIR, { recursive: true });
  if (process.platform === 'win32') {
    execFileSync('powershell', ['-NoProfile', '-Command',
      `Expand-Archive -Path '${zip}' -DestinationPath '${RUFFLE_DIR}' -Force`], { stdio: 'inherit' });
  } else {
    execFileSync('unzip', ['-o', '-q', zip, '-d', RUFFLE_DIR], { stdio: 'inherit' });
  }
  fs.unlinkSync(zip);
  console.log('ruffle web build ready at public/ruffle');
})();
