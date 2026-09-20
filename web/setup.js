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
const URL = `https://github.com/ruffle-rs/ruffle/releases/download/${RUFFLE_VERSION}/${ZIP_NAME}`;

function get(url, dest) {
  return new Promise((resolve, reject) => {
    https.get(url, { headers: { 'User-Agent': 'vibeskua-ruffle-test' } }, res => {
      if ([301, 302, 307, 308].includes(res.statusCode)) {
        res.resume();
        return resolve(get(res.headers.location, dest));
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

  if (fs.existsSync(path.join(RUFFLE_DIR, 'ruffle.js'))) {
    console.log('ruffle web build already present, skipping download');
    return;
  }

  const zip = path.join(ROOT, ZIP_NAME);
  console.log(`downloading ${URL}`);
  await get(URL, zip);
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
