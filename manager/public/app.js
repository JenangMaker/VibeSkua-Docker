'use strict';
// VibeSkua Manager page. Everything goes through this server's /api/, which
// relays to VibeSkua's tab host API (Skua.App.Avalonia/TabHostWindow.Api.cs).
// Text from the game or the bot is only ever set as textContent.

const $ = sel => document.querySelector(sel);
const POLL_MS = 4000;
const OPTIONS = [
  ['LagKiller', 'Lag Killer'], ['HidePlayers', 'Hide Players'], ['DisableFX', 'Disable FX'],
  ['SkipCutscenes', 'Skip Cutscenes'], ['InfiniteRange', 'Infinite Range'], ['Magnetise', 'Magnetise'],
  ['HeadlessMode', 'Headless Mode'], ['UseFunctionBasedSkills', 'Function-based Skills'], ['StreamerMode', 'Streamer Mode'],
];

// ---- helpers ------------------------------------------------------------------

function h(tag, props = {}, ...children) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(props)) {
    if (v === undefined || v === null || v === false) continue;
    if (k === 'class') el.className = v;
    else if (k === 'text') el.textContent = v;
    else if (k.startsWith('on')) el.addEventListener(k.slice(2), v);
    else el.setAttribute(k, v === true ? '' : v);
  }
  for (const c of children.flat()) if (c !== null && c !== undefined && c !== false) el.append(c);
  return el;
}

class ApiError extends Error {
  constructor(status, message) { super(message); this.status = status; }
}

// Loaders for the requests a person starts (quiet: the 4 s poll's, which would
// keep them on): the bar along the top runs while any is out, after 150 ms so
// a quick one does not flash it, and the button that was clicked spins until
// its own are back. A click's handler asks before its first await, so the
// button is the one clicked in this same task.
let pending = 0, barTimer = null, clickedButton = null;
document.addEventListener('click', e => {
  clickedButton = e.target.closest?.('button') || null;
  setTimeout(() => { clickedButton = null; });
}, true);

function trackRequest() {
  // Not a button that opened a dialog: the dialog shows its own loader.
  const dialog = document.querySelector('dialog[open]');
  const btn = clickedButton?.isConnected && (!dialog || dialog.contains(clickedButton)) ? clickedButton : null;
  if (btn) {
    btn.dataset.busy = String((+btn.dataset.busy || 0) + 1);
    btn.classList.add('busy');
    btn.setAttribute('aria-busy', 'true');
  }
  if (pending++ === 0) barTimer = setTimeout(() => { $('#busy-bar').hidden = false; }, 150);
  return () => {
    if (btn) {
      const left = +btn.dataset.busy - 1;
      if (left > 0) btn.dataset.busy = String(left);
      else {
        delete btn.dataset.busy;
        btn.classList.remove('busy');
        btn.removeAttribute('aria-busy');
      }
    }
    if (--pending === 0) { clearTimeout(barTimer); $('#busy-bar').hidden = true; }
  };
}

async function api(method, path, body, { quiet = false } = {}) {
  const init = { method, headers: { 'X-Manager': '1' } };
  if (method !== 'GET') {
    init.headers['Content-Type'] = 'application/json';
    init.body = body === undefined ? '' : JSON.stringify(body);
  }
  const done = quiet ? null : trackRequest();
  try { return await request(path, init); } finally { done?.(); }
}

async function request(path, init) {
  const res = await fetch(path, init);
  let data = null;
  try { data = await res.json(); } catch { /* empty */ }
  if (res.status === 401 && path !== '/login') {
    // The session ended while the page was open (the manager restarted, an
    // update perhaps, or the login expired): reload rather than log in again
    // inside this copy, which may be an older version of the page.
    if (!$('#app-view').hidden) location.reload();
    else showLogin();
    throw new ApiError(401, 'Logged out');
  }
  if (!res.ok) throw new ApiError(res.status, data?.error || `${res.status} ${res.statusText}`);
  return data;
}

const q = encodeURIComponent;

function toast(message, bad = false) {
  const el = h('div', { class: `toast${bad ? ' bad' : ''}`, text: message });
  $('#toasts').append(el);
  setTimeout(() => el.remove(), bad ? 7000 : 3500);
}

// Runs an action, toasting how it went; returns its result (undefined on error).
async function act(label, fn) {
  try {
    const result = await fn();
    const err = result && typeof result === 'object' && !Array.isArray(result) && result.error;
    if (err) { toast(`${label}: ${err}`, true); return undefined; }
    toast(label);
    return result;
  } catch (e) {
    if (e.status !== 401) toast(`${label}: ${e.message}`, true);
    return undefined;
  }
}

const fmtMb = mb => mb == null ? '-' : mb >= 1024 ? `${(mb / 1024).toFixed(1)} GB` : `${Math.round(mb)} MB`;
const fmtCpu = c => c == null ? '-' : `${Math.round(c)}%`;
const fmtNum = n => n == null ? '-' : Number(n).toLocaleString();
const scriptName = p => p ? String(p).split(/[\\/]/).pop().replace(/\.cs$/i, '') : '';

function fmtUptime(seconds) {
  if (seconds == null) return '-';
  const d = Math.floor(seconds / 86400), hr = Math.floor(seconds % 86400 / 3600), m = Math.floor(seconds % 3600 / 60);
  return d ? `${d}d ${hr}h` : hr ? `${hr}h ${m}m` : `${m}m`;
}

// ---- streamer mode --------------------------------------------------------------
// As Skua's Streamer Mode does in the game: account and character names and
// room numbers are hidden on this page, so it can be shown on screen. A
// setting of this browser only; it also offers to turn on the game's own.

let streamer = false;
try { streamer = localStorage.getItem('vsm-streamer') === '1'; } catch { /* private window */ }

// Every name this page knows (accounts, characters), longest first so a
// name inside another is not half-replaced.
function knownNames() {
  const names = new Set();
  for (const t of state.tabs) { if (t.account) names.add(t.account); if (t.title && !/^Skua \d+$/.test(t.title)) names.add(t.title); }
  for (const s of Object.values(state.statuses)) if (s?.game?.player) names.add(s.game.player);
  for (const a of state.accounts?.accounts || []) if (a.user) names.add(a.user);
  return [...names].filter(n => n.length >= 2).sort((a, b) => b.length - a.length);
}

// Text with the known names (and the manager's own login) masked.
function anonText(text) {
  if (!streamer || !text) return text;
  let out = String(text);
  for (const name of knownNames()) out = out.split(name).join('[hidden]');
  const who = $('#who').dataset.user;
  if (who) out = out.split(who).join('[hidden]');
  // Room numbers: "yulgar-9721", "room 9721".
  return out.replace(/\b([a-z][\w]*)-\d{3,}\b/gi, '$1-****').replace(/\b(room\s*#?\s*)\d{3,}\b/gi, '$1****');
}

// Masks a text field's value as dots, like a password field, but without
// the browser offering to save it: CSS where the browser has it (Chrome,
// Edge, Safari), else a password field. Unmask with masked = false.
const CSS_MASK = typeof CSS !== 'undefined' && CSS.supports('-webkit-text-security', 'disc');

function setMasked(input, masked) {
  if (CSS_MASK) {
    input.classList.toggle('masked', masked);
  } else {
    if (!input.dataset.type) input.dataset.type = input.type;
    input.type = masked ? 'password' : input.dataset.type;
  }
}

// A map name without its room number ("yulgar-9721" -> "yulgar").
const anonMap = map => (streamer && map ? String(map).replace(/-\d+$/, '') : map);

function setStreamer(on, fromUser) {
  streamer = on;
  document.body.classList.toggle('streamer', on);
  $('#streamer').checked = on;
  try { localStorage.setItem('vsm-streamer', on ? '1' : '0'); } catch { /* private window */ }
  $('#who').textContent = on ? '' : $('#who').dataset.user || '';
  if (fromUser && on && confirm("Also turn on the game's own Streamer Mode in every tab (names, guild and room number in the game)?"))
    armyAll('Streamer Mode on', '/api/army/option?name=StreamerMode&value=true', true);
  if (logTab !== null) { logSince = 0; $('#log-text').textContent = ''; pollLog(); }
  render();
}

$('#streamer').addEventListener('change', e => setStreamer(e.target.checked, true));

// ---- login ----------------------------------------------------------------------

let polling = null;

function showLogin() {
  stopPolling();
  $('#app-view').hidden = true;
  $('#login-view').hidden = false;
  $('#login-form [name=user]').focus();
}

async function showApp(session) {
  $('#login-view').hidden = true;
  $('#app-view').hidden = false;
  $('#who').dataset.user = session.user;
  setStreamer(streamer, false);
  startPolling();
}

$('#login-form').addEventListener('submit', async e => {
  e.preventDefault();
  const form = e.target;
  const button = form.querySelector('button');
  button.disabled = true;
  $('#login-error').textContent = '';
  try {
    await api('POST', '/login', { user: form.user.value, password: form.password.value });
    form.password.value = '';
    showApp(await api('GET', '/api/session'));
  } catch (err) {
    $('#login-error').textContent = err.message;
  } finally {
    button.disabled = false;
  }
});

$('#logout').addEventListener('click', async () => {
  try { await api('POST', '/logout'); } catch { /* logged out anyway */ }
  showLogin();
});

// ---- views ----------------------------------------------------------------------

let view = 'bots';
for (const btn of document.querySelectorAll('.view-tab')) {
  btn.addEventListener('click', () => {
    view = btn.dataset.view;
    for (const b of document.querySelectorAll('.view-tab')) {
      b.classList.toggle('active', b === btn);
      b.setAttribute('aria-selected', String(b === btn));
    }
    for (const s of document.querySelectorAll('.view')) s.hidden = s.id !== `view-${view}`;
    refresh();
  });
}

// ---- polling --------------------------------------------------------------------

let state = { host: null, tabs: [], statuses: {}, resources: null, accounts: null, grid: false };
let refreshing = false;
let loaded = false;   // the first refresh is done (the placeholders are gone)

function startPolling() {
  stopPolling();
  refresh();
  polling = setInterval(() => { if (document.visibilityState === 'visible') refresh(); }, POLL_MS);
}

function stopPolling() {
  if (polling) clearInterval(polling);
  polling = null;
}

document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible' && polling) refresh(); });

async function refresh() {
  if (refreshing) return;
  refreshing = true;
  // The first load shows the bar; the poll after it does not.
  const opts = { quiet: loaded };
  try {
    const [host, tabs, resources] = await Promise.all([
      api('GET', '/api/status', undefined, opts), api('GET', '/api/tabs', undefined, opts), api('GET', '/api/resources', undefined, opts),
    ]);
    state.host = host;
    state.tabs = tabs;
    state.resources = resources;
    setConnected(true, host);
    if (view === 'bots') {
      const statuses = await Promise.all(tabs.map(t =>
        t.running ? api('GET', `/api/tabs/${t.tab}/api/status?detail=1`, undefined, opts).catch(() => null) : null));
      state.statuses = Object.fromEntries(tabs.map((t, i) => [t.tab, statuses[i]]));
    }
    if (view === 'accounts' || state.accounts === null) state.accounts = await api('GET', '/api/accounts', undefined, opts);
    loaded = true;
    render();
  } catch (e) {
    if (e.status !== 401) setConnected(false, null, e.message);
  } finally {
    refreshing = false;
  }
}

function setConnected(ok, host, error) {
  $('#conn-dot').className = `dot ${ok ? 'ok' : 'bad'}`;
  $('#conn-text').textContent = ok
    ? `VibeSkua ${host.version || ''} | up ${fmtUptime(host.uptimeSeconds)} | ${host.tabs} tab${host.tabs === 1 ? '' : 's'}`
    : `Not connected: ${error}`;
}

function render() {
  renderSummary();
  if (view === 'bots') renderCards();
  if (view === 'accounts') renderAccounts();
  if (view === 'resources') renderResources();
}

function renderSummary() {
  const r = state.resources;
  const el = $('#res-summary');
  el.replaceChildren();
  if (!r) return;
  const mem = r.memory || {};
  el.append(
    h('span', {}, 'CPU ', h('b', { text: fmtCpu(r.total?.cpu) }), ` of ${r.cpus * 100}%`),
    h('span', {}, 'Memory ', h('b', { text: fmtMb(mem.containerMb ?? r.total?.memoryMb) })),
    h('span', {}, 'Load ', h('b', { text: (r.load || []).map(v => v.toFixed(2)).join(' ') || '-' })),
  );
}

// ---- bot cards ------------------------------------------------------------------

const cards = new Map();   // tab number -> card
// Tabs ticked on their card: the whole Army bar acts on these only (on
// every tab when none is). Kept while the page is open.
const picked = new Set();

// The ticked tabs that are running, in order.
const pickedRunning = () => state.tabs.filter(t => t.running && picked.has(t.tab)).map(t => t.tab);
// "tab 2" / "tabs 2, 4", for titles and questions.
const tabList = tabs => `tab${tabs.length === 1 ? '' : 's'} ${tabs.join(', ')}`;

// Each Army button's two labels: for every tab ("Start all", "Jump..."),
// and for the ticked ones ("Start (2 selected)", "Jump (2 selected)...").
const ARMY_BUTTONS = ['load-all', 'restart-all', 'jump-all', 'options-all'].map(id => document.getElementById(id))
  .concat([...document.querySelectorAll('[data-army]')]);
for (const b of ARMY_BUTTONS) {
  b.dataset.labelAll = b.textContent;
  b.dataset.labelSome = b.textContent.replace(/ all$/, '').replace(/\.\.\.$/, '');
  b.dataset.dots = b.textContent.endsWith('...') ? '...' : '';
}

// With tabs ticked the bar says so once, in place of its "Army" label (a chip
// with its own clear button), and the buttons drop their "all".
function updatePickUi() {
  const n = picked.size;
  for (const b of ARMY_BUTTONS) b.textContent = n ? `${b.dataset.labelSome}${b.dataset.dots}` : b.dataset.labelAll;
  $('#army-label').hidden = n > 0;
  $('#pick-info').hidden = n === 0;
  $('.army').classList.toggle('picking', n > 0);
  $('#pick-count').textContent = `${tabList([...picked].sort((a, b) => a - b)).replace(/^t/, 'T')} selected`;
}

// ---- Execute (phones) -------------------------------------------------------------
// The panel closes once an action is picked, on a click outside, and on Escape.

function setExecOpen(open) {
  $('#army-actions').classList.toggle('open', open);
  $('#army-exec').setAttribute('aria-expanded', String(open));
}

$('#army-exec').addEventListener('click', () => setExecOpen(!$('#army-actions').classList.contains('open')));
$('#army-actions').addEventListener('click', e => { if (e.target.closest('button')) setExecOpen(false); });
document.addEventListener('click', e => {
  if ($('#army-actions').classList.contains('open') && !e.target.closest('#army-actions, #army-exec')) setExecOpen(false);
});
document.addEventListener('keydown', e => {
  if (e.key === 'Escape' && $('#army-actions').classList.contains('open')) { setExecOpen(false); $('#army-exec').focus(); }
});

$('#pick-clear').addEventListener('click', () => {
  picked.clear();
  for (const card of cards.values()) { card.r.pick.checked = false; card.el.classList.remove('picked'); }
  updatePickUi();
});

function renderCards() {
  const container = $('#cards');
  const seen = new Set();
  // The host marks the tab on the desktop as selected, and none while its
  // Grid View shows them all: running tabs with none selected is Grid View.
  state.grid = state.tabs.some(t => t.running) && !state.tabs.some(t => t.selected);
  $('#grid-toggle').textContent = state.grid ? 'Grid View: on' : 'Grid View';
  $('#grid-toggle').setAttribute('aria-pressed', String(state.grid));
  for (const tab of state.tabs) {
    seen.add(tab.tab);
    let card = cards.get(tab.tab);
    if (!card) {
      card = makeCard(tab.tab);
      cards.set(tab.tab, card);
    }
    updateCard(card, tab, state.statuses[tab.tab]);
  }
  for (const [n, card] of cards) if (!seen.has(n)) { card.el.remove(); cards.delete(n); picked.delete(n); }
  updatePickUi();
  // In tab order.
  const ordered = [...cards.entries()].sort((a, b) => a[0] - b[0]).map(([, c]) => c.el);
  if (ordered.some((el, i) => container.children[i] !== el)) container.replaceChildren(...ordered);
  // Until the first refresh is in, "Loading the tabs..." rather than "No tabs."
  $('#cards-loading').hidden = loaded;
  $('#no-tabs').hidden = !loaded || state.tabs.length > 0;
}

function makeCard(n) {
  const r = {};
  const field = (key, label) => [h('dt', { text: label }), r[key] = h('dd')];
  const stat = (key, label) => h('div', {}, r[key] = h('b', { text: '0' }), h('span', { text: label }));
  // A bar is a progress bar to assistive tech (setBar keeps its value).
  const bar = (cls, label) => {
    const fill = h('i'); const text = h('span');
    r[`${cls}Fill`] = fill; r[`${cls}Text`] = text;
    return r[`${cls}Bar`] = h('div', { class: `bar ${cls}`, role: 'progressbar', 'aria-label': label, 'aria-valuemin': '0', 'aria-valuemax': '100', 'aria-valuenow': '0' }, fill, text);
  };
  r.pick = h('input', { type: 'checkbox', class: 'card-pick', title: 'Select: Load script (army bar) goes to the selected tabs only', 'aria-label': `Select tab ${n}` });
  const el = h('article', { class: 'card' },
    h('div', { class: 'card-head' },
      r.pick,
      h('span', { class: 'card-num', text: `Tab ${n}` }),
      r.name = h('span', { class: 'card-name' })),
    // Its state, and Shown on the tab the VibeSkua desktop shows (the Show
    // button puts a tab there): under the name, which keeps the whole row.
    h('div', { class: 'card-tags' },
      r.pill = h('span', { class: 'pill' }),
      r.shown = h('span', { class: 'shown-badge', text: 'Shown', title: 'This tab is the one shown on the VibeSkua desktop', hidden: true })),
    h('dl', { class: 'kv' }, field('map', 'Map'), field('room', 'Room'), field('level', 'Level'), field('gold', 'Gold'), field('script', 'Script')),
    h('div', { class: 'bars' }, bar('hp', 'HP'), bar('mp', 'MP')),
    r.fight = h('div', { class: 'fight' },
      h('div', { class: 'fight-head' }, h('span', { class: 'muted', text: 'Target' }), r.targetName = h('b'), r.targetPct = h('span', { class: 'muted' })),
      bar('target', 'Target HP'),
      r.cellMons = h('div', { class: 'cell-mons' })),
    r.questList = h('div', { class: 'quests' }),
    // Closed by default (the card stays short); open or closed, it stays so.
    r.equip = h('details', { class: 'equip', hidden: true },
      r.equipSummary = h('summary'),
      r.equipList = h('ul', { class: 'equip-list' })),
    h('div', { class: 'stats' }, stat('kills', 'Kills'), stat('drops', 'Drops'), stat('quests', 'Quests'), stat('deaths', 'Deaths'), stat('relogins', 'Relogins')),
    r.usage = h('div', { class: 'usage' }),
    h('div', { class: 'actions' },
      r.startStop = h('button', { class: 'small primary', onclick: () => startStop(n) }),
      h('button', { class: 'small', onclick: () => openScriptDialog([n]) }, 'Load...'),
      r.optionsBtn = h('button', { class: 'small', title: "The loaded script's options", onclick: () => openScriptOptions(n) }, 'Script options...'),
      h('button', { class: 'small', title: "This tab's Skua options (Lag Killer, Hide Players, Headless Mode...)", onclick: () => openSkuaOptions(n) }, 'Skua options...'),
      r.loginBtn = h('button', { class: 'small', onclick: () => logInOut(n) }),
      h('button', { class: 'small', onclick: () => openLog(n) }, 'Log'),
      h('button', { class: 'small', title: 'Show this tab on the VibeSkua desktop', onclick: () => act(`Tab ${n} shown`, () => api('POST', `/api/tabs/${n}/select`)) }, 'Show'),
      h('button', { class: 'small', title: "Restart this tab's Skua (a running script stops; the game logs back in if it was restarted too)", onclick: () => restartTab(n, false) }, 'Restart'),
      h('button', { class: 'small', title: 'Restart Skua and reload the game page (logs in again)', onclick: () => restartTab(n, true) }, 'Reload game'),
      h('button', { class: 'small danger', onclick: () => closeTab(n) }, 'Close')));
  r.pick.addEventListener('change', () => {
    if (r.pick.checked) picked.add(n); else picked.delete(n);
    el.classList.toggle('picked', r.pick.checked);
    updatePickUi();
  });
  return { el, r, running: false };
}

function updateCard(card, tab, status) {
  const { r } = card;
  const game = status?.game;
  const script = status?.script;
  const stats = status?.stats;
  card.el.classList.toggle('selected', tab.selected);
  // Shown: the tab on the desktop; In grid: every running tab, in Grid View.
  r.shown.hidden = !(tab.selected || (state.grid && tab.running));
  r.shown.textContent = state.grid ? 'In grid' : 'Shown';
  r.shown.title = state.grid ? "The desktop's Grid View shows every tab, this one included" : 'This tab is the one shown on the VibeSkua desktop';
  r.name.textContent = streamer ? `Player ${tab.tab}` : (game?.loggedIn && game.player) || tab.account || tab.title;

  let pill = ['Not running', 'bad'];
  if (tab.running && !status) pill = ['Starting', 'warn'];
  else if (status && !status.bridgeConnected) pill = ['Game not connected', 'warn'];
  else if (game && !game.loggedIn) pill = [tab.account ? 'Logged out' : 'No account', 'warn'];
  else if (game?.loggedIn) pill = script?.running ? ['Running script', 'ok'] : ['Logged in', 'ok'];
  if (status?.throttle?.headless) pill[0] += ' (headless)';
  r.pill.textContent = pill[0];
  r.pill.className = `pill ${pill[1]}`;

  const loggedIn = !!game?.loggedIn;
  r.map.textContent = loggedIn ? `${anonMap(game.map) || '-'}${game.cell ? ` (${game.cell})` : ''}` : '-';
  // The room (game.room: VibeSkua after 1.2.0); hidden on stream, as in the logs.
  r.room.textContent = !loggedIn || !game.room ? '-' : streamer ? 'hidden' : game.room;
  r.level.textContent = loggedIn ? `${game.level ?? '-'}${game.className ? ` - ${game.className}` : ''}` : '-';
  r.gold.textContent = loggedIn ? fmtNum(game.gold) : '-';
  r.script.textContent = script?.loaded ? `${scriptName(script.loaded)}${script.running ? ' (running)' : ' (loaded)'}` : 'none';
  r.script.title = script?.loaded || '';

  const pct = (a, b) => b ? Math.max(0, Math.min(100, a / b * 100)) : 0;
  setBar(r, 'hp', loggedIn ? pct(game.hp, game.maxHp) : 0, loggedIn ? `HP ${fmtNum(game.hp)} / ${fmtNum(game.maxHp)}` : 'HP');
  setBar(r, 'mp', loggedIn ? pct(game.mp, game.maxMp) : 0, loggedIn ? `MP ${fmtNum(game.mp)} / ${fmtNum(game.maxMp)}` : 'MP');

  updateFight(r, loggedIn ? status?.combat : null);
  updateQuests(r, loggedIn ? status?.quests : null);
  updateEquipment(r, loggedIn ? status?.equipment : null);

  r.kills.textContent = fmtNum(stats?.kills ?? 0);
  r.drops.textContent = fmtNum(stats?.drops ?? 0);
  r.quests.textContent = fmtNum(stats?.questsCompleted ?? 0);
  r.deaths.textContent = fmtNum(stats?.deaths ?? 0);
  r.relogins.textContent = fmtNum(stats?.relogins ?? 0);

  r.usage.replaceChildren(
    h('span', { text: `Skua ${fmtCpu(tab.skua?.cpu)} / ${fmtMb(tab.skua?.memoryMb)}` }),
    h('span', { text: `Game ${fmtCpu(tab.page?.cpu)} / ${fmtMb(tab.page?.memoryMb)}` }),
    h('span', { text: `Restarts ${tab.restarts}` }),
  );

  // Log in or Log out, whichever applies; not until the tab's Skua answers.
  card.loggedIn = loggedIn;
  r.loginBtn.textContent = loggedIn ? 'Log out' : 'Log in';
  r.loginBtn.title = loggedIn ? 'Log this account out (a running script stops)' : "Log this tab's account in";
  r.loginBtn.disabled = !status;

  card.running = !!script?.running;
  r.startStop.textContent = card.running ? 'Stop' : 'Start';
  r.startStop.disabled = !status || (!card.running && !script?.loaded);
  r.startStop.title = !card.running && !script?.loaded ? 'Load a script first' : '';
  r.optionsBtn.disabled = !status || !script?.loaded;
}

// A card's bar: its fill, its text, and the value a screen reader reads.
function setBar(r, cls, pct, text) {
  r[`${cls}Fill`].style.width = `${pct}%`;
  r[`${cls}Text`].textContent = text;
  r[`${cls}Bar`].setAttribute('aria-valuenow', String(Math.round(pct)));
  if (text) r[`${cls}Bar`].setAttribute('aria-valuetext', text);
  else r[`${cls}Bar`].removeAttribute('aria-valuetext');
}

// The target with its HP, and the cell's monsters: alive ones first, the
// target's kind marked, dead ones (state 0) dimmed.
function updateFight(r, combat) {
  const target = combat?.target;
  const mons = combat?.monsters || [];
  r.fight.hidden = !target && !mons.length;
  if (r.fight.hidden) return;
  const pct = target?.maxHp ? Math.max(0, Math.min(100, target.hp / target.maxHp * 100)) : 0;
  r.targetName.textContent = target ? target.name : 'none';
  r.targetPct.textContent = target ? `${pct.toFixed(pct < 10 ? 1 : 0)}%` : '';
  setBar(r, 'target', pct, target ? `${fmtNum(target.hp)} / ${fmtNum(target.maxHp)}` : '');

  // Group same-named monsters: "Binky", "Treeant x3 (2 alive)".
  const groups = new Map();
  for (const m of mons) {
    const g = groups.get(m.name) || { name: m.name, total: 0, alive: 0 };
    g.total++;
    if (m.state !== 0 && m.hp > 0) g.alive++;
    groups.set(m.name, g);
  }
  r.cellMons.replaceChildren(...[...groups.values()]
    .sort((a, b) => b.alive - a.alive || a.name.localeCompare(b.name))
    .map(g => h('span', {
      class: `mon${g.alive ? '' : ' dead'}${target && g.name === target.name ? ' targeted' : ''}`,
      text: g.total > 1 ? `${g.name} x${g.total}${g.alive !== g.total ? ` (${g.alive} alive)` : ''}` : g.name,
    })));
}

// Quests in progress: each with its requirements as have/need, finished ones
// dimmed. "ready": everything collected; "auto": the script turns it in.
const MAX_QUESTS = 3;

function updateQuests(r, quests) {
  r.questList.hidden = !quests?.length;
  if (r.questList.hidden) return;
  const shown = quests.slice(0, MAX_QUESTS).map(q => {
    const done = q.requirements.filter(x => x.have >= x.need).length;
    return h('div', { class: 'quest' },
      h('div', { class: 'quest-head' },
        h('b', { text: q.name, title: `Quest ${q.id}` }),
        q.ready ? h('span', { class: 'tag ok', text: 'ready' }) : null,
        q.registered ? h('span', { class: 'tag', text: 'auto', title: 'The script turns it in when ready' }) : null,
        h('span', { class: 'muted', text: `${done}/${q.requirements.length}` })),
      h('ul', { class: 'reqs' }, q.requirements.map(x => h('li', { class: x.have >= x.need ? 'done' : null },
        h('span', { class: 'req-name', text: x.name || `Item ${x.id}`, title: x.temp ? 'Temporary item' : null }),
        h('span', { class: 'req-count', text: `${fmtNum(x.have)}/${fmtNum(x.need)}` })))));
  });
  const more = quests.length - MAX_QUESTS;
  r.questList.replaceChildren(
    h('div', { class: 'quests-title muted', text: quests.length === 1 ? 'Quest' : `Quests (${quests.length})` }),
    ...shown,
    ...(more > 0 ? [h('div', { class: 'muted small', text: `+${more} more` })] : []));
}

// What the character wears (status.equipment: VibeSkua after 1.2.0): slot,
// item and its enhancement, and the special one (proc) if any. Rebuilt only
// when it changes.
function updateEquipment(r, items) {
  r.equip.hidden = !items?.length;
  if (r.equip.hidden) return;
  const key = JSON.stringify(items);
  if (r.equip.dataset.key === key) return;
  r.equip.dataset.key = key;
  const weapon = items.find(i => i.slot === 'Weapon');
  r.equipSummary.replaceChildren(
    h('span', { text: 'Equipment' }),
    weapon ? h('span', { class: 'muted equip-peek', text: weapon.name }) : null);
  r.equipList.replaceChildren(...items.map(i => h('li', {},
    h('span', { class: 'equip-slot muted', text: i.slot }),
    h('span', { class: 'equip-name', text: i.name, title: i.name }),
    i.enhancement ? h('span', { class: 'tag', text: i.enhancement, title: 'Enhancement' }) : null,
    i.proc ? h('span', { class: 'tag ok', text: i.proc, title: 'Special enhancement' }) : null)));
}

async function startStop(n) {
  const card = cards.get(n);
  if (card.running) await act(`Tab ${n}: script stopped`, () => api('POST', `/api/tabs/${n}/api/script/stop`));
  else await act(`Tab ${n}: script started`, () => api('POST', `/api/tabs/${n}/api/script/start`));
  refresh();
}

// One tab's Log in / Log out, as the Army bar's do for every tab.
async function logInOut(n) {
  const card = cards.get(n);
  if (card.loggedIn) {
    if (!confirm(`Log out tab ${n}? A running script stops.`)) return;
    await act(`Tab ${n}: logged out`, () => api('POST', `/api/tabs/${n}/api/army/logout`));
  } else {
    await act(`Tab ${n}: logging in`, () => api('POST', `/api/tabs/${n}/api/army/login`));
  }
  refresh();
}

async function restartTab(n, game) {
  const what = game ? 'restart its Skua and reload its game (it logs in again)' : "restart its Skua (a running script stops)";
  if (!confirm(`Tab ${n}: ${what}?`)) return;
  await act(`Tab ${n} restarting`, () => api('POST', `/api/tabs/${n}/restart${game ? '?game=1' : ''}`));
  refresh();
}

async function closeTab(n) {
  if (!confirm(`Close tab ${n}? Its Skua and game stop.`)) return;
  await act(`Tab ${n} closed`, () => api('POST', `/api/tabs/${n}/close`));
  refresh();
}

// ---- army -----------------------------------------------------------------------

for (const btn of document.querySelectorAll('[data-army]')) {
  btn.addEventListener('click', async () => {
    if (btn.dataset.confirm) {
      const targets = picked.size ? pickedRunning() : null;
      const question = targets ? btn.dataset.confirm.replace('every account', tabList(targets)) : btn.dataset.confirm;
      if (!confirm(question)) return;
    }
    btn.disabled = true;
    try { await armyAll(btn.dataset.labelSome, `/api/army/${btn.dataset.army}`); }
    finally { btn.disabled = false; refresh(); }
  });
}

// An Army command (path: /api/army/...): for every tab in one call, or, with
// cards ticked, for each ticked running tab through its own API (every:
// always every tab). One toast, listing the tabs that failed.
async function armyAll(label, path, every = false) {
  try {
    let results;
    if (picked.size && !every) {
      const targets = pickedRunning();
      if (!targets.length) { toast(`${label}: none of the selected tabs is running`, true); return; }
      const sub = path.slice('/api'.length);   // /army/...
      results = Object.fromEntries(await Promise.all(targets.map(n =>
        api('POST', `/api/tabs/${n}/api${sub}`).then(r => [n, r ?? {}], e => [n, { error: e.message }]))));
    } else {
      results = await api('POST', path);
    }
    const failed = Object.entries(results || {}).filter(([, r]) => !r || r.error);
    if (failed.length) toast(`${label}: failed in tab ${failed.map(([t, r]) => `${t} (${r?.error || 'no answer'})`).join(', ')}`, true);
    else toast(`${label}: done in ${Object.keys(results || {}).length} tab(s)`);
  } catch (e) {
    if (e.status !== 401) toast(`${label}: ${e.message}`, true);
  }
}

// Restart Skua in the selected tabs (every running tab when none is), as
// each card's Restart does for its own.
$('#restart-all').addEventListener('click', async () => {
  const running = state.tabs.filter(t => t.running).map(t => t.tab);
  const targets = picked.size ? running.filter(n => picked.has(n)) : running;
  if (!targets.length) { toast(picked.size ? 'None of the selected tabs is running' : 'No running tab to restart', true); return; }
  const which = targets.length === running.length && !picked.size ? 'every tab' : `tab${targets.length === 1 ? '' : 's'} ${targets.join(', ')}`;
  if (!confirm(`Restart Skua in ${which}? Running scripts stop.`)) return;
  const btn = $('#restart-all');
  btn.disabled = true;
  try {
    const results = await Promise.all(targets.map(n =>
      api('POST', `/api/tabs/${n}/restart`).then(r => [n, r?.error], e => [n, e.message])));
    const failed = results.filter(([, err]) => err);
    if (failed.length) toast(`Restart: failed in ${failed.map(([n, err]) => `tab ${n} (${err})`).join(', ')}`, true);
    else toast(`Restarting ${targets.length} tab(s)`);
  } finally {
    btn.disabled = false;
    refresh();
  }
});

$('#open-tab').addEventListener('click', async () => {
  await act('Tab opened', () => api('POST', '/api/tabs'));
  refresh();
});

$('#grid-toggle').addEventListener('click', async () => {
  state.grid = !state.grid;
  await act(state.grid ? 'Grid View on' : 'Grid View off', () => api('POST', `/api/grid?on=${state.grid ? 1 : 0}`));
  refresh();
});

$('#load-all').addEventListener('click', () => {
  const running = state.tabs.filter(t => t.running).map(t => t.tab);
  if (!picked.size) { openScriptDialog(running); return; }
  const targets = running.filter(n => picked.has(n));
  if (!targets.length) { toast('None of the selected tabs is running', true); return; }
  openScriptDialog(targets);
});

$('#jump-all').addEventListener('click', () => {
  const dlg = $('#dlg-jump');
  const targets = picked.size ? pickedRunning() : null;
  if (targets && !targets.length) { toast('None of the selected tabs is running', true); return; }
  dlg.querySelector('h2').textContent = targets ? `Jump ${tabList(targets)}` : 'Jump every account';
  dlg.querySelector('form').reset();
  dlg.returnValue = '';   // Escape keeps the last one
  dlg.showModal();
});

$('#dlg-jump').addEventListener('close', () => {
  const dlg = $('#dlg-jump');
  if (dlg.returnValue !== 'go') return;
  const f = dlg.querySelector('form');
  const map = f.map.value.trim(), cell = f.cell.value.trim(), player = f.player.value.trim();
  if (player) armyAll(`Jump to ${player}`, `/api/army/goto?player=${q(player)}`);
  else if (map || cell) armyAll(`Jump to ${map || cell}`, `/api/army/jump?map=${q(map)}&cell=${q(cell)}`);
});

// Skua's options (Army Control's Misc Options). For every tab: On / Off
// buttons, as the tabs may differ. For one tab: a checkbox each, showing its
// current value; an older VibeSkua that cannot tell gets the buttons too.
function onOffRows(label, send) {
  return OPTIONS.map(([name, text]) => h('div', { class: 'opt' },
    h('span', { text }),
    h('button', { type: 'button', class: 'small', onclick: () => send(name, true, `${label}${text} on`) }, 'On'),
    h('button', { type: 'button', class: 'small', onclick: () => send(name, false, `${label}${text} off`) }, 'Off')));
}

$('#options-all').addEventListener('click', () => {
  const targets = picked.size ? pickedRunning() : null;
  if (targets && !targets.length) { toast('None of the selected tabs is running', true); return; }
  $('#options-title').textContent = targets ? `Skua options for ${tabList(targets)}` : 'Skua options for every tab';
  $('#options-info').textContent = '';
  $('#options-list').replaceChildren(...onOffRows('', (name, on, label) => armyAll(label, `/api/army/option?name=${name}&value=${on}`)));
  $('#dlg-options').showModal();
});

let skuaOptionsTab = null;

function setTabOption(n, name, on, label) {
  return act(label, () => api('POST', `/api/tabs/${n}/api/army/option?name=${name}&value=${on}`));
}

async function openSkuaOptions(n) {
  skuaOptionsTab = n;
  $('#options-title').textContent = `Skua options, tab ${n}`;
  $('#options-info').textContent = 'Reading...';
  $('#options-info').classList.add('loading');
  $('#options-list').replaceChildren();
  $('#dlg-options').showModal();
  let values = null;
  try {
    values = await api('GET', `/api/tabs/${n}/api/army/options`);
  } catch (e) {
    if (e.status === 401) return;
  }
  if (skuaOptionsTab !== n) return;
  $('#options-info').classList.remove('loading');
  if (!values || values.error || typeof values.LagKiller !== 'boolean') {
    $('#options-info').textContent = 'This VibeSkua cannot tell the current values (update its image); set each one on or off.';
    $('#options-list').replaceChildren(...onOffRows(`Tab ${n}: `, (name, on, label) => setTabOption(n, name, on, label)));
    return;
  }
  $('#options-info').textContent = 'Each change applies right away.';
  $('#options-list').replaceChildren(...OPTIONS.map(([name, text]) => {
    const box = h('input', { type: 'checkbox', id: `skopt-${name}` });
    box.checked = values[name];
    box.addEventListener('change', async () => {
      box.disabled = true;
      const ok = await setTabOption(n, name, box.checked, `Tab ${n}: ${text} ${box.checked ? 'on' : 'off'}`);
      if (ok === undefined) box.checked = !box.checked;
      box.disabled = false;
    });
    return h('div', { class: 'opt' }, h('label', { for: box.id, text }), box);
  }));
}

$('#dlg-options').addEventListener('close', () => { skuaOptionsTab = null; });

// ---- script dialog --------------------------------------------------------------

let scriptTargets = [];
let searchTimer = null;

function openScriptDialog(tabs) {
  if (!tabs.length) { toast('No running tab to load a script in', true); return; }
  scriptTargets = tabs;
  $('#script-title').textContent = tabs.length === 1 ? `Load script in tab ${tabs[0]}`
    : tabs.length <= 6 ? `Load script in tabs ${tabs.join(', ')}` : `Load script in ${tabs.length} tabs`;
  $('#script-search').value = '';
  $('#script-category').value = 'All';
  $('#script-path').value = '';
  $('#script-list').replaceChildren();
  $('#dlg-script').returnValue = '';
  $('#dlg-script').showModal();
  setScriptMode(scriptMode);
}

// ---- script dialog: browse ------------------------------------------------------
// The Scripts folder on disk, folder by folder, as a file picker shows it:
// folders first, then scripts; paths are what loading takes. The folder
// last looked at is kept while the page is open.

let scriptMode = 'search', browseDir = '', browseSeq = 0;

function setScriptMode(mode) {
  scriptMode = mode;
  for (const b of document.querySelectorAll('.smode-tab')) {
    b.classList.toggle('active', b.dataset.smode === mode);
    b.setAttribute('aria-selected', String(b.dataset.smode === mode));
  }
  $('.search-row').hidden = mode !== 'search';
  $('#script-crumbs').hidden = mode !== 'browse';
  if (mode === 'browse') browseScripts(browseDir);
  else { searchScripts(); $('#script-search').focus(); }
}

for (const b of document.querySelectorAll('.smode-tab'))
  b.addEventListener('click', () => setScriptMode(b.dataset.smode));

// A list entry that works from the keyboard too: Tab reaches it, Enter or
// Space does what a click does.
function activatable(li, onActivate) {
  li.tabIndex = 0;
  li.setAttribute('role', 'button');
  li.addEventListener('click', onActivate);
  li.addEventListener('keydown', e => {
    if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onActivate(); }
  });
  return li;
}

function scriptItem(list, s, label, description) {
  const li = h('li', { title: s.path },
    h('div', { text: label }),
    h('small', { text: description || 'No description provided.' }),
    h('small', { class: 'path', text: s.path }));
  activatable(li, () => {
    for (const x of list.children) x.classList.toggle('active', x === li);
    $('#script-path').value = s.path;
  });
  li.addEventListener('dblclick', () => { $('#script-path').value = s.path; $('#dlg-script').close('load'); });
  return li;
}

function renderCrumbs(dir) {
  const parts = dir ? dir.split('/') : [];
  const crumb = (text, path, last) => h('button', { type: 'button', disabled: last, onclick: () => browseScripts(path) }, text);
  const items = [crumb('Scripts', '', parts.length === 0)];
  parts.forEach((part, i) => {
    items.push(h('span', { class: 'sep', text: '/' }));
    items.push(crumb(part, parts.slice(0, i + 1).join('/'), i === parts.length - 1));
  });
  $('#script-crumbs').replaceChildren(...items);
}

async function browseScripts(dir) {
  const list = $('#script-list');
  const seq = ++browseSeq;
  renderCrumbs(dir);
  list.replaceChildren(h('li', { class: 'none loading', text: 'Reading the folder...' }));
  let reply;
  try {
    reply = await api('GET', `/api/tabs/${scriptTargets[0]}/api/scripts/browse?dir=${q(dir)}`);
  } catch (e) {
    if (e.status === 401 || seq !== browseSeq || scriptMode !== 'browse') return;
    list.replaceChildren(h('li', { class: 'none', text: e.status === 404 || /not found/i.test(e.message)
      ? 'This VibeSkua cannot list its folders yet: update its image. Search still works.'
      : e.message }));
    return;
  }
  if (seq !== browseSeq || scriptMode !== 'browse') return;
  if (reply?.error) {
    if (dir) { browseDir = ''; browseScripts(''); return; }   // the folder went away
    list.replaceChildren(h('li', { class: 'none', text: reply.error }));
    return;
  }
  browseDir = reply.dir;
  renderCrumbs(reply.dir);
  const items = [];
  if (reply.dir) {
    const parent = reply.dir.split('/').slice(0, -1).join('/');
    const up = h('li', { class: 'up', title: 'Up one folder' }, h('div', { text: '..' }), h('small', { text: 'Up one folder' }));
    items.push(activatable(up, () => browseScripts(parent)));
  }
  for (const f of reply.folders) {
    const li = h('li', { class: 'folder', title: f.path },
      h('div', { text: f.name }),
      h('small', { text: `${f.scripts} script${f.scripts === 1 ? '' : 's'}` }));
    items.push(activatable(li, () => browseScripts(f.path)));
  }
  for (const s of reply.files)
    items.push(scriptItem(list, s, s.name || s.file.replace(/.cs$/i, ''), s.description));
  if (!reply.folders.length && !reply.files.length) items.push(h('li', { class: 'none', text: 'This folder has no scripts.' }));
  list.replaceChildren(...items);
  list.scrollTop = 0;
}

$('#script-search').addEventListener('input', () => {
  clearTimeout(searchTimer);
  searchTimer = setTimeout(searchScripts, 250);
});

$('#script-category').addEventListener('change', () => { clearTimeout(searchTimer); searchScripts(); });

const SCRIPTS_SHOWN = 60;
let searchSeq = 0;

// As the Search Scripts window: by name, without the unnamed Core*.cs
// libraries (the index's "null"), in the chosen category. VibeSkua does
// that itself; doing it here as well keeps an older VibeSkua's reply right.
async function searchScripts() {
  const term = $('#script-search').value.trim();
  const category = $('#script-category').value;
  const list = $('#script-list');
  const seq = ++searchSeq;
  // The old results stay, dimmed, while a new search runs; an empty list says so.
  list.setAttribute('aria-busy', 'true');
  if (!list.querySelector('li:not(.none)')) list.replaceChildren(h('li', { class: 'none loading', text: 'Searching...' }));
  try {
    const params = `limit=500${term ? `&q=${q(term)}` : ''}${category !== 'All' ? `&category=${q(category)}` : ''}`;
    const reply = await api('GET', `/api/tabs/${scriptTargets[0]}/api/scripts?${params}`);
    if (seq !== searchSeq || scriptMode !== 'search') return;   // a newer search, or the Browse view
    const known = v => (v && v !== 'null' ? v : '');
    const inCategory = s => category === 'All'
      || (category === 'Local'
        ? (s.tags || []).includes('Local')
        : s.path.split('/').some(part => part.toLowerCase().startsWith(category.toLowerCase())));
    const scripts = reply.filter(s => known(s.name) && inCategory(s))
      .sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }));
    if (!scripts.length) { list.replaceChildren(h('li', { class: 'none', text: 'No scripts found.' })); return; }
    const items = scripts.slice(0, SCRIPTS_SHOWN).map(s => scriptItem(list, s, s.name, known(s.description)));
    if (scripts.length > SCRIPTS_SHOWN)
      items.push(h('li', { class: 'none', text: `${scripts.length - SCRIPTS_SHOWN} more: type more of the name, or pick a category.` }));
    list.replaceChildren(...items);
  } catch (e) {
    if (e.status !== 401 && seq === searchSeq && scriptMode === 'search') list.replaceChildren(h('li', { class: 'none', text: e.message }));
  } finally {
    if (seq === searchSeq) list.removeAttribute('aria-busy');
  }
}

$('#dlg-script').addEventListener('close', async () => {
  const action = $('#dlg-script').returnValue;
  const path = $('#script-path').value.trim();
  if (!(action === 'load' || action === 'start') || !path) return;
  const verb = action === 'start' ? 'start' : 'load';
  const label = `${action === 'start' ? 'Started' : 'Loaded'} ${scriptName(path)}`;
  const results = await Promise.all(scriptTargets.map(n =>
    api('POST', `/api/tabs/${n}/api/script/${verb}?path=${q(path)}`).then(r => [n, r?.error], e => [n, e.message])));
  const failed = results.filter(([, err]) => err);
  if (failed.length) toast(`${label}: failed in ${failed.map(([n, err]) => `tab ${n} (${err})`).join(', ')}`, true);
  else toast(`${label} in ${results.length} tab(s)`);
  refresh();
});

// ---- script options dialog ------------------------------------------------------
// The loaded script's options, as the Script Loader's Options button shows
// them: grouped (Options, then CoreBots' and other groups), each by its type.

let soptsTab = null, soptsData = null, soptsControls = [];

async function openScriptOptions(n) {
  soptsTab = n;
  soptsData = null;
  soptsControls = [];
  soptsRevealed = false;
  $('#sopts-reveal').hidden = true;
  $('#sopts-title').textContent = `Script options, tab ${n}`;
  $('#sopts-info').textContent = 'Compiling the script to read its options...';
  $('#sopts-info').classList.add('loading');
  $('#sopts-body').replaceChildren();
  $('#sopts-error').textContent = '';
  $('.sopts-skip').hidden = true;
  $('#sopts-skip-note').textContent = '';
  $('#sopts-save').disabled = $('#sopts-defaults').disabled = true;
  $('#dlg-sopts').showModal();
  try {
    const data = await api('GET', `/api/tabs/${n}/api/script/options`);
    if (soptsTab !== n) return;
    if (data.error) { $('#sopts-info').textContent = ''; $('#sopts-error').textContent = data.error; return; }
    soptsData = data;
    renderScriptOptions(data);
  } catch (e) {
    if (e.status !== 401) { $('#sopts-info').textContent = ''; $('#sopts-error').textContent = e.message; }
  } finally {
    if (soptsTab === n) $('#sopts-info').classList.remove('loading');
  }
}

function renderScriptOptions(data) {
  const file = data.file.split(/[\\/]/).pop();
  $('#sopts-info').textContent = `${scriptName(data.script)}: saved in options/${file}` +
    (data.editable ? '' : '. The script is running: stop it to change its options.');
  // Older VibeSkua versions do not report it: no checkbox then.
  if (data.skipWindow !== undefined) {
    $('.sopts-skip').hidden = false;
    $('#sopts-skip').checked = data.skipWindow || data.skipAll;
    $('#sopts-skip').disabled = data.skipAll;
    $('#sopts-skip-note').textContent = data.skipAll
      ? 'Skipped for every script (SKUA_SKIP_SCRIPT_OPTIONS).'
      : 'The script then runs with the options saved here, without asking. Applies right away.';
  }
  if (!data.options.length) {
    $('#sopts-body').replaceChildren(h('p', { class: 'muted', text: 'This script has no options.' }));
    return;
  }
  const groups = new Map();
  for (const o of data.options) {
    if (!groups.has(o.group)) groups.set(o.group, []);
    groups.get(o.group).push(o);
  }
  soptsControls = [];
  const sections = [...groups].map(([group, options]) => h('section', { class: 'sopt-group' },
    h('h3', { text: group }),
    options.map(o => {
      const control = optionControl(o, !data.editable);
      soptsControls.push({ option: o, control });
      return h('div', { class: 'sopt' },
        h('div', { class: 'sopt-text' },
          h('label', { for: control.id, text: o.displayName }),
          o.description && o.description !== o.displayName ? h('small', { class: 'muted', text: o.description }) : null),
        control);
    })));
  $('#sopts-body').replaceChildren(...sections);
  $('#sopts-save').disabled = $('#sopts-defaults').disabled = !data.editable;
  updateReveal();
}

let soptId = 0;

function optionControl(o, readOnly) {
  const id = `sopt-${++soptId}`;
  let el;
  if (o.type === 'bool') {
    el = h('input', { id, type: 'checkbox' });
    el.checked = /^true$/i.test(o.value);
  } else if (o.type === 'enum') {
    el = h('select', { id }, o.values.map(v => h('option', { value: v, text: v })));
    el.value = o.values.find(v => v.toLowerCase() === String(o.value).toLowerCase()) ?? o.values[0];
  } else if (maskOption(o)) {
    // Masked: a text field (numbers too, which VibeSkua checks on Save),
    // dots until Reveal values.
    el = h('input', { id, type: 'text', autocomplete: 'off', spellcheck: 'false', inputmode: o.type === 'text' ? null : 'decimal' });
    el.value = o.value;
    el.dataset.masked = '1';
    setMasked(el, true);
  } else {
    el = h('input', { id, type: o.type === 'text' ? 'text' : 'number', step: o.type === 'int' ? '1' : 'any' });
    el.value = o.value;
  }
  el.disabled = readOnly;
  return el;
}

// Options holding a player's or account's name are masked like passwords
// (UltrasLW's "Player 1" ... "Player 4", say): by their label, or because
// the value is one of this instance's account or character names. In
// streamer mode every text and number option is (the room number too).
const NAME_OPTION = /player|account|user|character/i;

function maskOption(o) {
  if (!['text', 'int', 'number'].includes(o.type)) return false;
  if (streamer) return true;
  if (o.type !== 'text') return false;
  const value = String(o.value || '').toLowerCase();
  return NAME_OPTION.test(`${o.name} ${o.displayName}`)
    || (value.length > 0 && knownNames().some(n => n.toLowerCase() === value));
}

let soptsRevealed = false;

function updateReveal() {
  const masked = soptsControls.filter(c => c.control.dataset.masked);
  $('#sopts-reveal').hidden = masked.length === 0;
  $('#sopts-reveal').textContent = soptsRevealed ? 'Hide values' : 'Reveal values';
  for (const { control } of masked) setMasked(control, !soptsRevealed);
}

$('#sopts-reveal').addEventListener('click', () => {
  soptsRevealed = !soptsRevealed;
  updateReveal();
});

const controlValue = ({ option, control }) =>
  option.type === 'bool' ? (control.checked ? 'True' : 'False') : control.value;

$('#sopts-defaults').addEventListener('click', () => {
  for (const { option, control } of soptsControls) {
    if (option.type === 'bool') control.checked = /^true$/i.test(option.default);
    else control.value = option.default;
  }
});

$('#sopts-form').addEventListener('submit', async e => {
  e.preventDefault();
  if (!soptsData?.editable || soptsTab === null) return;
  const values = soptsControls.map(c => ({ category: c.option.category, name: c.option.name, value: controlValue(c) }));
  const button = $('#sopts-save');
  button.disabled = true;
  $('#sopts-error').textContent = '';
  try {
    const result = await api('POST', `/api/tabs/${soptsTab}/api/script/options`, { values });
    if (result.error) { $('#sopts-error').textContent = result.error; return; }
    toast(`Tab ${soptsTab}: script options saved`);
    $('#dlg-sopts').close('saved');
  } catch (err) {
    if (err.status !== 401) $('#sopts-error').textContent = err.message;
  } finally {
    button.disabled = !soptsData?.editable;
  }
});

$('#sopts-skip').addEventListener('change', async e => {
  const box = e.target, tab = soptsTab, skip = box.checked;
  if (tab === null) return;
  box.disabled = true;
  try {
    const result = await api('POST', `/api/tabs/${tab}/api/script/options`, { skipWindow: skip });
    if (result.error) throw new Error(result.error);
    toast(skip ? `Tab ${tab}: the options window will not open at start` : `Tab ${tab}: the options window opens at start again`);
  } catch (err) {
    box.checked = !skip;
    if (err.status !== 401) $('#sopts-error').textContent = err.message;
  } finally {
    box.disabled = false;
  }
});

$('#dlg-sopts').addEventListener('close', () => { soptsTab = null; });

// ---- log dialog -----------------------------------------------------------------

let logTab = null, logSince = 0, logTimer = null;

function openLog(n) {
  logTab = n;
  logSince = 0;
  $('#log-title').textContent = `Tab ${n} log`;
  $('#log-text').textContent = '';
  $('#log-loading').hidden = false;
  $('#dlg-log').showModal();
  pollLog();
  logTimer = setInterval(pollLog, 2000);
}

// Every 2 s: only the first read of a log shows the loaders.
async function pollLog() {
  if (logTab === null) return;
  const first = !$('#log-loading').hidden;
  try {
    const log = await api('GET', `/api/tabs/${logTab}/api/log?type=${$('#log-type').value}&since=${logSince}`, undefined, { quiet: !first });
    $('#log-loading').hidden = true;
    const pre = $('#log-text');
    if (log.total < logSince) { pre.textContent = ''; logSince = 0; return; }   // cleared
    if (log.lines.length) {
      pre.append(anonText(log.lines.join('\n')) + '\n');
      logSince = log.total;
      if ($('#log-follow').checked) pre.scrollTop = pre.scrollHeight;
    }
  } catch { /* try again next time */ }
}

$('#log-type').addEventListener('change', () => { logSince = 0; $('#log-text').textContent = ''; $('#log-loading').hidden = false; pollLog(); });
$('#log-close').addEventListener('click', () => $('#dlg-log').close());
$('#dlg-log').addEventListener('close', () => { clearInterval(logTimer); logTab = null; });

// ---- accounts -------------------------------------------------------------------

function renderAccounts() {
  const data = state.accounts;
  if (!data) return;
  $('#accounts-file').textContent = data.file;
  const body = $('#accounts-body');
  if (!data.accounts.length) {
    body.replaceChildren(h('tr', {}, h('td', { colspan: '8', class: 'muted', text: 'No accounts yet.' })));
    return;
  }
  body.replaceChildren(...data.accounts.map(a => h('tr', {},
    h('td', { text: String(a.tab) }),
    h('td', { text: streamer ? `Account ${a.tab}` : a.user || '' }),
    h('td', {}, h('span', { class: 'pill', text: a.source === 'env' ? 'environment' : 'added here' })),
    h('td', { text: a.server || 'default' }),
    h('td', { text: a.script ? scriptName(a.script) : '-', title: a.script || undefined }),
    h('td', { text: a.autoStart == null ? '-' : a.autoStart ? 'yes' : 'no' }),
    h('td', {}, h('span', {
      class: `pill ${a.loggedIn ? 'ok' : a.open ? 'warn' : ''}`,
      text: a.loggedIn ? 'logged in' : a.open ? 'open, logged out' : 'no tab',
    })),
    h('td', { class: 'actions-cell' },
      // A closed tab opens again with its number, and logs its account in.
      a.open ? null : h('button', { class: 'small primary', title: `Open tab ${a.tab}; it logs this account in`, onclick: () => openAccountTab(a) }, 'Open tab'),
      ...(a.editable ? [
        h('button', { class: 'small', onclick: () => openAccountDialog(a) }, 'Edit'),
        h('button', { class: 'small danger', onclick: () => deleteAccount(a) }, 'Remove'),
      ] : [h('span', { class: 'muted small', text: 'set in the environment' })])))));
}

async function openAccountTab(a) {
  await act(`Tab ${a.tab} opened`, () => api('POST', `/api/tabs?tab=${a.tab}`));
  state.accounts = null;
  refresh();
}

let editing = null;

function openAccountDialog(account) {
  editing = account || null;
  const f = $('#account-form');
  f.reset();
  $('#account-error').textContent = '';
  $('#account-title').textContent = account ? `Edit tab ${account.tab}'s account` : 'Add account';
  f.tab.value = account ? account.tab : nextFreeTab();
  f.tab.readOnly = !!account;
  f.user.value = account?.user || '';
  setMasked(f.user, streamer);   // typed as dots on stream
  f.server.value = account?.server || '';
  f.script.value = account?.script || '';
  f.autoStart.checked = !!account?.autoStart;
  // Skua options for a new account only: turned on once, at its first login.
  $('#account-options').hidden = !!account;
  $('#account-options-list').replaceChildren(...(account ? [] : OPTIONS.map(([name, text]) =>
    h('label', { class: 'check' }, h('input', { type: 'checkbox', 'data-option': name }), text))));
  f.pass.required = !account;
  $('#pass-hint').textContent = account ? 'Leave empty to keep the saved password.' : 'Stored on the VibeSkua server; never shown again.';
  $('#dlg-account').showModal();
  (account ? f.user : f.tab).focus();
}

function nextFreeTab() {
  const used = new Set([...(state.accounts?.accounts || []).map(a => a.tab), ...state.tabs.map(t => t.tab)]);
  for (let n = 1; n <= 50; n++) if (!used.has(n)) return n;
  return '';
}

$('#add-account').addEventListener('click', () => openAccountDialog(null));

// Submitted by Save or Enter, after the browser checked the required fields.
$('#account-form').addEventListener('submit', async e => {
  e.preventDefault();
  const f = $('#account-form');
  const n = Number(f.tab.value);
  const body = {
    user: f.user.value.trim(),
    pass: f.pass.value || undefined,
    server: f.server.value.trim() || undefined,
    script: f.script.value.trim() || undefined,
    autoStart: f.autoStart.checked,
  };
  if (!editing && state.tabs.some(t => t.tab === n) &&
      !confirm(`Tab ${n} is open. Saving restarts it and logs this account in. Continue?`)) return;
  // A changed login restarts an open tab (see PUT /accounts in the tab host API).
  const loginChanged = editing && (f.pass.value || body.user !== editing.user || (body.server || null) !== (editing.server || null));
  if (loginChanged && editing.open && !confirm(`Saving restarts tab ${n} so it logs in again. Continue?`)) return;
  const options = editing ? [] : [...document.querySelectorAll('#account-options-list input:checked')].map(b => b.dataset.option);
  const button = $('#account-save');
  button.disabled = true;
  try {
    const result = await api('PUT', `/api/accounts/${n}`, body);
    f.pass.value = '';
    $('#dlg-account').close('saved');
    toast(`Tab ${n}: ${result.applied}`);
    // This page's server waits for the login, so closing the page is fine.
    if (options.length) {
      const names = options.map(o => OPTIONS.find(([k]) => k === o)?.[1] || o).join(', ');
      act(`Tab ${n}: ${names} will be turned on once it logs in`,
        () => api('POST', '/api/manager/initial-options', { tab: n, options }));
    }
    state.accounts = null;
    refresh();
  } catch (err) {
    // Shown next to Save and focused, so it is seen and read out.
    $('#account-error').textContent = err.message;
    $('#account-error').focus();
  } finally {
    button.disabled = false;
  }
});

async function deleteAccount(a) {
  if (!confirm(`Remove ${streamer ? 'the account' : a.user} of tab ${a.tab}? Its tab closes.`)) return;
  await act(`Tab ${a.tab}: account removed`, () => api('DELETE', `/api/accounts/${a.tab}`));
  state.accounts = null;
  refresh();
}

// ---- resources ------------------------------------------------------------------

const KIND_LABELS = { skua: 'Skua (the bots)', pages: 'Game pages', gpu: 'Electron GPU process', electron: 'Electron (rest)', desktop: 'Desktop and other' };

// Widths set through the style object: the page's CSP refuses style attributes.
function meterFill(fraction) {
  const i = h('i');
  i.style.width = `${Math.min(100, fraction * 100).toFixed(1)}%`;
  return i;
}

function renderResources() {
  const r = state.resources;
  if (!r) return;
  const mem = r.memory || {};
  const statCard = (label, value, fraction) => h('div', { class: 'stat' },
    h('span', { text: label }), h('b', { text: value }),
    fraction == null ? null : h('div', {
      class: 'meter', role: 'progressbar', 'aria-label': label, 'aria-valuemin': '0', 'aria-valuemax': '100',
      'aria-valuenow': String(Math.round(Math.min(1, fraction) * 100)), 'aria-valuetext': value,
    }, meterFill(fraction)));
  const memLimit = mem.containerLimitMb ?? mem.hostTotalMb;
  $('#res-cards').replaceChildren(
    statCard(`CPU (${r.cpus} cores)`, fmtCpu(r.total?.cpu), (r.total?.cpu || 0) / (r.cpus * 100)),
    statCard(mem.containerLimitMb ? 'Memory (container limit)' : 'Memory (container)', `${fmtMb(mem.containerMb)}${memLimit ? ` / ${fmtMb(memLimit)}` : ''}`,
      memLimit && mem.containerMb ? mem.containerMb / memLimit : null),
    statCard('Host memory free', fmtMb(mem.hostAvailableMb), null),
    statCard('Load (1, 5, 15 min)', (r.load || []).map(v => v.toFixed(2)).join('  ') || '-', null),
  );
  const kinds = Object.entries(r.kinds || {}).sort((a, b) => b[1].cpu - a[1].cpu);
  $('#kinds-body').replaceChildren(...kinds.map(([k, v]) => h('tr', {},
    h('td', { text: KIND_LABELS[k] || k }), h('td', { text: String(v.processes) }),
    h('td', { text: fmtCpu(v.cpu) }), h('td', { text: fmtMb(v.memoryMb) }))));
  $('#top-body').replaceChildren(...(r.top || []).map(p => h('tr', {},
    h('td', { text: String(p.pid) }), h('td', { text: p.name }), h('td', { text: KIND_LABELS[p.kind] || p.kind }),
    h('td', { text: fmtCpu(p.cpu) }), h('td', { text: fmtMb(p.memoryMb) }))));
}

// ---- dialogs ----------------------------------------------------------------------

for (const btn of document.querySelectorAll('[data-close]')) {
  btn.addEventListener('click', () => btn.closest('dialog').close('cancel'));
}

// A click on the backdrop (outside the dialog's box) closes it, as Cancel
// does. Only when the press began there too: selecting text in a field and
// letting go outside the box must not close it. Not the dialogs with fields
// to fill in (data-keep-open): a stray tap would lose what was typed.
for (const dlg of document.querySelectorAll('dialog:not([data-keep-open])')) {
  const outside = e => {
    if (e.target !== dlg) return false;
    const r = dlg.getBoundingClientRect();
    return e.clientX < r.left || e.clientX > r.right || e.clientY < r.top || e.clientY > r.bottom;
  };
  let pressedOutside = false;
  dlg.addEventListener('pointerdown', e => { pressedOutside = outside(e); });
  dlg.addEventListener('click', e => {
    if (pressedOutside && outside(e)) dlg.close('cancel');
    pressedOutside = false;
  });
}

// Enter in the search box searches now rather than submitting the dialog.
$('#script-search').addEventListener('keydown', e => {
  if (e.key === 'Enter') { e.preventDefault(); clearTimeout(searchTimer); searchScripts(); }
});

// ---- start ----------------------------------------------------------------------

(async () => {
  try { showApp(await api('GET', '/api/session')); } catch { showLogin(); }
})();
