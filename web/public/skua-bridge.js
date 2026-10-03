// Page side of the Skua bridge (Skua.Ruffle/RuffleBridge.cs on the other end).
//
// Skua.Core drives skua.swf through ExternalInterface callbacks. With the
// Flash ActiveX control that was CallFunction/FlashCall; here the host sends
// JSON over a WebSocket, this file runs it against the Ruffle player, and the
// SWF's own ExternalInterface.call()s are forwarded back as events.
//
//   host -> page  {"id":1,"fn":"getGameObject","args":["world.strMapName"]}
//   page -> host  {"id":1,"ok":true,"value":...} | {"id":1,"ok":false,"error":"..."}
//   page -> host  {"ev":"packetFromServer","args":[...]}
//
// Transport is pluggable (setTransport) so it can also be driven by a test
// relay; connect(url) is the normal WebSocket one.
(function () {
  // Names skua.swf passes to ExternalInterface.call (Externalizer.call,
  // Main.as), plus the older ones Skua.Core still handles.
  const EVENTS = ['requestLoadGame', 'pre-load', 'debug', 'openWebsite',
                  'packetFromServer', 'loaded', 'pext', 'packet'];
  const MAX_QUEUED = 2000;

  const bridge = {
    player: null,
    transport: null,
    queued: [],
    stats: { calls: 0, errors: 0, events: 0, callMs: 0 },

    attach(player) { this.player = player; },

    // Wraps the page's own handlers for the SWF's calls (debug, requestLoadGame
    // ...) so each is also forwarded. Call after those handlers are defined;
    // the SWF looks them up by name on every call, so this works any time.
    install() {
      for (const name of EVENTS) {
        const local = window[name];
        if (local && local.__skuaBridge) continue;
        const wrapped = function (...args) {
          bridge.stats.events++;
          bridge.send({ ev: name, args });
          return typeof local === 'function' ? local.apply(this, args) : true;
        };
        wrapped.__skuaBridge = true;
        window[name] = wrapped;
      }
    },

    setTransport(transport) {
      this.transport = transport;
      if (transport) {
        const pending = this.queued;
        this.queued = [];
        for (const text of pending) transport.send(text);
      }
    },

    send(message) {
      const text = JSON.stringify(message);
      if (this.transport) this.transport.send(text);
      else if (message.ev && this.queued.length < MAX_QUEUED) this.queued.push(text);
    },

    // One message from the host.
    receive(text) {
      let m;
      try { m = JSON.parse(text); } catch (e) { return; }
      if (typeof m.id !== 'number' || typeof m.fn !== 'string') return;
      const t0 = performance.now();
      let reply;
      try {
        // "page.<name>" is one of the page's own functions (window.vibeskua,
        // index.html), not the SWF's.
        const target = m.fn.startsWith('page.') ? window.vibeskua : this.player;
        const name = m.fn.startsWith('page.') ? m.fn.slice(5) : m.fn;
        const fn = target && target[name];
        if (typeof fn !== 'function') throw new Error('no SWF callback named ' + m.fn);
        const value = fn.apply(target, Array.isArray(m.args) ? m.args : []);
        reply = { id: m.id, ok: true, value: value === undefined ? null : value };
      } catch (e) {
        this.stats.errors++;
        reply = { id: m.id, ok: false, error: String((e && e.message) || e) };
      }
      this.stats.calls++;
      this.stats.callMs += performance.now() - t0;
      this.send(reply);
    },

    // Normal transport: a WebSocket to Skua.Host, reconnecting with backoff.
    connect(url, log = () => {}) {
      let delay = 1000;
      const open = () => {
        const ws = new WebSocket(url);
        ws.onopen = () => {
          delay = 1000;
          log('skua bridge connected: ' + url);
          this.setTransport({ send: text => ws.send(text) });
        };
        ws.onmessage = e => this.receive(e.data);
        ws.onclose = () => {
          if (this.transport) log('skua bridge disconnected; retrying');
          this.setTransport(null);
          setTimeout(open, delay);
          delay = Math.min(delay * 2, 15000);
        };
      };
      open();
    },
  };

  window.skuaBridge = bridge;
})();
