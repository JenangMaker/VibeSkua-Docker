// WebSocket <-> TCP bridge.
//
// Ruffle running in a browser context cannot open raw TCP sockets, but the AQW
// client talks to SmartFoxServer over plain TCP. Ruffle's `socketProxy` config
// lets each (host, port) the SWF uses be redirected to a WebSocket URL; this
// server terminates that WebSocket and opens the real TCP connection.
//
// Path format: ws://127.0.0.1:8181/<host>/<port>
//
// UNTESTED against a live game server. The bridge itself is straightforward,
// but whether Ruffle's socketProxy wire format matches this byte-for-byte has
// not been confirmed. See web/README.md.

const { WebSocketServer } = require('ws');
const net = require('net');

// Only ever dial Artix. Without this the container would be an open relay for
// anything that can reach the proxy port.
const ALLOWED_HOST = /^[a-z0-9-]+\.(aq|aqworlds)\.com$/i;

function start({ port = 8181, host = '127.0.0.1', log = console.log } = {}) {
  const wss = new WebSocketServer({ host, port });
  let seq = 0;

  wss.on('connection', (ws, req) => {
    const id = ++seq;
    const m = /^\/([^/]+)\/(\d+)$/.exec(req.url || '');
    if (!m) {
      log(`[proxy #${id}] bad path ${req.url}`);
      return ws.close(1008, 'expected /<host>/<port>');
    }

    const targetHost = decodeURIComponent(m[1]);
    const targetPort = Number(m[2]);
    if (!ALLOWED_HOST.test(targetHost) || targetPort < 1 || targetPort > 65535) {
      log(`[proxy #${id}] refused ${targetHost}:${targetPort}`);
      return ws.close(1008, 'host not allowed');
    }

    log(`[proxy #${id}] connecting ${targetHost}:${targetPort}`);
    const tcp = net.connect(targetPort, targetHost);
    tcp.setNoDelay(true);

    const closeBoth = why => {
      if (why) log(`[proxy #${id}] ${why}`);
      try { tcp.destroy(); } catch {}
      try { ws.close(); } catch {}
    };

    tcp.on('connect', () => log(`[proxy #${id}] connected`));
    tcp.on('data', chunk => { if (ws.readyState === ws.OPEN) ws.send(chunk); });
    tcp.on('error', e => closeBoth(`tcp error: ${e.message}`));
    tcp.on('close', () => closeBoth('tcp closed'));

    ws.on('message', data => {
      // ws gives Buffer for binary frames; coerce strings just in case.
      tcp.write(Buffer.isBuffer(data) ? data : Buffer.from(data));
    });
    ws.on('error', e => closeBoth(`ws error: ${e.message}`));
    ws.on('close', () => closeBoth('ws closed'));
  });

  wss.on('listening', () => log(`[proxy] listening on ws://${host}:${port}`));
  return wss;
}

module.exports = { start };
