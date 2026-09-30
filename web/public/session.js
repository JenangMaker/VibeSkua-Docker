// Session keeping for the game client: auto-login, re-login after a
// disconnect, and periodic recycling (see web/main.js for the settings).
//
// Recycling exists because Ruffle keeps every SWF it has loaded for the whole
// session (each map and every player's gear, reloaded on each room change),
// so memory and frame cost only grow. A page reload frees all of it; this
// waits until the character is out of combat, remembers the map and cell,
// reloads, logs back in and returns there.
(function () {
  const RETURN_KEY = 'vibeskua.returnTo';
  // After a disconnect, how long the login screen must sit untouched before
  // this logs in itself rather than leave it to Skua (see tick).
  const RELOGIN_GRACE_MS = 60000;
  const sleep = ms => new Promise(r => setTimeout(r, ms));

  const session = {
    player: null,
    say: () => {},
    cfg: {},
    startedAt: Date.now(),
    mapChanges: 0,
    lastPlace: null,
    playing: false,
    busy: false,
    recycling: false,

    has(path) {
      try { return this.player.isNull(path) === 'false'; } catch (e) { return false; }
    },
    get(path) {
      try { return JSON.parse(this.player.getGameObject(path)); } catch (e) { return null; }
    },
    loggedIn() {
      try { return this.player.isLoggedIn() === 'true' && !!this.get('world.strMapName'); } catch (e) { return false; }
    },

    start(cfg, player, say) {
      this.cfg = cfg || {};
      this.player = player;
      this.say = say || this.say;
      if (this.cfg.autoLogin) this.login();
      else if (sessionStorage.getItem(RETURN_KEY)) sessionStorage.removeItem(RETURN_KEY);
      setInterval(() => this.tick(), 3000);
      setInterval(() => this.watchContext(), 3000);
      const parts = [];
      if (this.cfg.autoLogin) parts.push('auto-login on');
      if (this.cfg.recycleAfterMinutes) parts.push(`recycle after ${this.cfg.recycleAfterMinutes} min`);
      if (this.cfg.recycleAfterMapChanges) parts.push(`recycle after ${this.cfg.recycleAfterMapChanges} map changes`);
      if (parts.length) this.say('session: ' + parts.join(', '));
    },

    // Same steps as Skua's login: game.login(user, pass), then pick a server.
    async login() {
      if (this.busy) return;
      this.busy = true;
      try {
        const res = await fetch('/autologin?instance=' + (window.VIBESKUA_INSTANCE || '0'), { headers: { 'X-Vibeskua': '1' }, cache: 'no-store' });
        if (!res.ok) return;
        const creds = await res.json();
        for (let i = 0; i < 90 && !this.has('mcLogin.ni'); i++) await sleep(1000);
        if (!this.has('mcLogin.ni')) { this.say('auto-login: login screen never appeared', 'err'); return; }
        this.player.callGameFunction('login', creds.user, creds.pass);

        let listed = 0;
        for (let i = 0; i < 40 && !listed; i++) {
          await sleep(1000);
          if (this.has('mcLogin.sl.iList')) listed = this.get('mcLogin.sl.iList.numChildren') || 0;
        }
        if (!listed) { this.say('auto-login: no server list - wrong credentials?', 'err'); return; }
        await sleep(1000);

        let server = null;
        if (creds.server && String(this.player.clickServer(creds.server)) === 'true') server = creds.server;
        if (!server) {
          const data = await (await fetch('/servers')).json();
          for (const s of data.servers || []) {
            if (!s.sName || s.bOnline === 0 || s.bOnline === false) continue;
            if (String(this.player.clickServer(s.sName)) === 'true') { server = s.sName; break; }
          }
        }
        if (!server) { this.say('auto-login: could not pick a server', 'err'); return; }

        for (let i = 0; i < 60 && !this.loggedIn(); i++) await sleep(1000);
        if (!this.loggedIn()) { this.say('auto-login: server ' + server + ' did not let us in', 'err'); return; }
        this.say('auto-login: in on ' + server, 'ok');
        const recycled = await this.returnToSaved();
        // A script Skua was running was left mid-loop in the game that went
        // away; tell Skua so it restarts it (Skua.Linux/ScriptKeeper.cs).
        // Not after the first login: auto-start covers that.
        const reason = recycled ? 'recycle' : this.reloginReason;
        this.reloginReason = null;
        if (reason && window.skuaBridge && window.skuaBridge.transport)
          window.skuaBridge.send({ ev: 'vibeskua.relogged', args: [reason] });
      } catch (e) {
        this.say('auto-login failed: ' + e, 'err');
      } finally {
        this.busy = false;
      }
    },

    async returnToSaved() {
      const saved = JSON.parse(sessionStorage.getItem(RETURN_KEY) || 'null');
      sessionStorage.removeItem(RETURN_KEY);
      if (!saved || !saved.map) return false;
      await sleep(3000);
      if (this.get('world.strMapName') !== saved.map) {
        this.join(saved.map, saved.cell, saved.pad);
        for (let i = 0; i < 30 && this.get('world.strMapName') !== saved.map; i++) await sleep(1000);
      }
      // The trip back is not a map change the next recycle should count.
      this.lastPlace = null;
      this.mapChanges = 0;
      this.say(`recycle: back to ${saved.map} (${saved.cell})`, 'ok');
      return true;
    },

    join(map, cell = 'Enter', pad = 'Spawn') {
      const user = this.get('world.myAvatar.objData.strUsername');
      const room = this.get('world.curRoom');
      this.player.callGameFunction('sfc.sendString', `%xt%zm%cmd%${room}%tfer%${user}%${map}%${cell}%${pad}%`);
    },

    tick() {
      if (this.busy || this.recycling) return;
      const inGame = this.loggedIn();
      if (inGame) {
        if (this.disconnectedAt) {
          this.say('session: back in (Skua logged in again)', 'ok');
          this.disconnectedAt = 0;
        }
        this.playing = true;
        const place = this.get('world.strMapName') + '#' + this.get('world.curRoom');
        if (this.lastPlace && place !== this.lastPlace) this.mapChanges++;
        this.lastPlace = place;
        if (this.dueForRecycle()) this.recycle();
      } else if (this.playing && this.cfg.autoLogin) {
        // Kicked or disconnected. While one of its scripts runs, Skua logs
        // back in by itself (its AutoRelogin), and two logins at once trip
        // each other up (a script stopped for "not logged in", a login given
        // up on a server list that never came). So Skua gets the first go:
        // this logs in only once the login screen has sat untouched (no
        // server list, nothing connecting) for RELOGIN_GRACE_MS. Without
        // Skua attached it logs in straight away.
        const now = Date.now();
        const skua = !!(window.skuaBridge && window.skuaBridge.transport);
        const idle = this.has('mcLogin.ni') && !this.has('mcLogin.sl.iList');
        if (!this.disconnectedAt) {
          this.disconnectedAt = now;
          this.idleSince = now;
          this.say(skua ? 'session: disconnected; giving Skua the first try at logging back in'
                        : 'session: disconnected, logging back in', 'err');
        }
        if (!idle) this.idleSince = now;
        if (idle && (!skua || now - this.idleSince >= RELOGIN_GRACE_MS)) {
          if (skua) this.say('session: Skua did not log back in; logging in', 'err');
          this.playing = false;
          this.disconnectedAt = 0;
          this.reloginReason = 'relogin';
          this.login();
        }
      }
    },

    dueForRecycle() {
      if (!this.cfg.autoLogin) return false;   // could not log back in
      const minutes = (Date.now() - this.startedAt) / 60000;
      return (this.cfg.recycleAfterMinutes && minutes >= this.cfg.recycleAfterMinutes) ||
             (this.cfg.recycleAfterMapChanges && this.mapChanges >= this.cfg.recycleAfterMapChanges);
    },

    // Reload the client once the character is out of combat (intState 2 is
    // combat, as Skua's Player.InCombat), keeping map and cell to return to.
    // When Chromium's GPU process dies, every page loses its WebGL context.
    // Ruffle's WebGL renderer does not rebuild on a new one: it keeps drawing
    // into the dead one (thousands of "offscreen framebuffer incomplete"
    // errors, a broken picture) while the game itself runs on. Seen live after
    // a GPU process crash, and a recycle is what fixes it: reload the page
    // (out of combat, back to the same place; Skua restarts its script).
    // Checked every few seconds, as Ruffle may replace its canvas.
    watchContext() {
      const canvas = document.querySelector('ruffle-player')?.shadowRoot?.querySelector('canvas');
      if (!canvas || canvas === this.watchedCanvas) return;
      this.watchedCanvas = canvas;
      canvas.addEventListener('webglcontextlost', () => {
        if (this.recycling) return;
        if (!this.cfg.autoLogin) {
          this.say('session: the game lost its WebGL context (GPU process restarted); reload the page to fix the picture', 'err');
          return;
        }
        this.say('session: the game lost its WebGL context (GPU process restarted); reloading it', 'err');
        this.recycle('WebGL context lost');
      });
    },

    async recycle(reason = 'scheduled') {
      if (this.recycling) return;
      this.recycling = true;
      this.say(`recycle (${reason}): waiting to be out of combat`);
      for (let i = 0; i < 600 && this.get('world.myAvatar.dataLeaf.intState') === 2; i++) await sleep(1000);
      sessionStorage.setItem(RETURN_KEY, JSON.stringify({
        map: this.get('world.strMapName'),
        cell: this.get('world.strFrame') || 'Enter',
        pad: this.get('world.strPad') || 'Spawn',
      }));
      this.say('recycle: reloading the game client', 'ok');
      location.reload();
    },

    status() {
      return {
        minutes: +((Date.now() - this.startedAt) / 60000).toFixed(1),
        mapChanges: this.mapChanges,
        playing: this.playing,
        autoLogin: !!this.cfg.autoLogin,
        recycleAfterMinutes: this.cfg.recycleAfterMinutes || 0,
        recycleAfterMapChanges: this.cfg.recycleAfterMapChanges || 0,
      };
    },
  };

  window.vibeskuaSession = session;
})();
