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

async function api(method, path, body) {
  const init = { method, headers: { 'X-Manager': '1' } };
  if (method !== 'GET') {
    init.headers['Content-Type'] = 'application/json';
    init.body = body === undefined ? '' : JSON.stringify(body);
  }
  const res = await fetch(path, init);
  let data = null;
  try { data = await res.json(); } catch { /* empty */ }
  if (res.status === 401 && path !== '/login') { showLogin(); throw new ApiError(401, 'Logged out'); }
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

const fmtMb = mb => mb == null ? '–' : mb >= 1024 ? `${(mb / 1024).toFixed(1)} GB` : `${Math.round(mb)} MB`;
const fmtCpu = c => c == null ? '–' : `${Math.round(c)}%`;
const fmtNum = n => n == null ? '–' : Number(n).toLocaleString();
const scriptName = p => p ? String(p).split(/[\\/]/).pop().replace(/\.cs$/i, '') : '';

function fmtUptime(seconds) {
  if (seconds == null) return '–';
  const d = Math.floor(seconds / 86400), hr = Math.floor(seconds % 86400 / 3600), m = Math.floor(seconds % 3600 / 60);
  return d ? `${d}d ${hr}h` : hr ? `${hr}h ${m}m` : `${m}m`;
}

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
  $('#who').textContent = session.user;
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
    for (const b of document.querySelectorAll('.view-tab')) b.classList.toggle('active', b === btn);
    for (const s of document.querySelectorAll('.view')) s.hidden = s.id !== `view-${view}`;
    refresh();
  });
}

// ---- polling --------------------------------------------------------------------

let state = { host: null, tabs: [], statuses: {}, resources: null, accounts: null, grid: false };
let refreshing = false;

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
  try {
    const [host, tabs, resources] = await Promise.all([
      api('GET', '/api/status'), api('GET', '/api/tabs'), api('GET', '/api/resources'),
    ]);
    state.host = host;
    state.tabs = tabs;
    state.resources = resources;
    setConnected(true, host);
    if (view === 'bots') {
      const statuses = await Promise.all(tabs.map(t =>
        t.running ? api('GET', `/api/tabs/${t.tab}/api/status?detail=1`).catch(() => null) : null));
      state.statuses = Object.fromEntries(tabs.map((t, i) => [t.tab, statuses[i]]));
    }
    if (view === 'accounts' || state.accounts === null) state.accounts = await api('GET', '/api/accounts');
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
    ? `VibeSkua ${host.version || ''} · up ${fmtUptime(host.uptimeSeconds)} · ${host.tabs} tab${host.tabs === 1 ? '' : 's'}`
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
    h('span', {}, 'Load ', h('b', { text: (r.load || []).map(v => v.toFixed(2)).join(' ') || '–' })),
  );
}

// ---- bot cards ------------------------------------------------------------------

const cards = new Map();   // tab number -> card

function renderCards() {
  const container = $('#cards');
  const seen = new Set();
  for (const tab of state.tabs) {
    seen.add(tab.tab);
    let card = cards.get(tab.tab);
    if (!card) {
      card = makeCard(tab.tab);
      cards.set(tab.tab, card);
    }
    updateCard(card, tab, state.statuses[tab.tab]);
  }
  for (const [n, card] of cards) if (!seen.has(n)) { card.el.remove(); cards.delete(n); }
  // In tab order.
  const ordered = [...cards.entries()].sort((a, b) => a[0] - b[0]).map(([, c]) => c.el);
  if (ordered.some((el, i) => container.children[i] !== el)) container.replaceChildren(...ordered);
  $('#no-tabs').hidden = state.tabs.length > 0;
}

function makeCard(n) {
  const r = {};
  const field = (key, label) => [h('dt', { text: label }), r[key] = h('dd')];
  const stat = (key, label) => h('div', {}, r[key] = h('b', { text: '0' }), h('span', { text: label }));
  const bar = cls => { const fill = h('i'); const text = h('span'); r[`${cls}Fill`] = fill; r[`${cls}Text`] = text; return h('div', { class: `bar ${cls}` }, fill, text); };
  const el = h('article', { class: 'card' },
    h('div', { class: 'card-head' },
      h('span', { class: 'card-num', text: `Tab ${n}` }),
      r.name = h('span', { class: 'card-name' }),
      r.pill = h('span', { class: 'pill' })),
    h('dl', { class: 'kv' }, field('map', 'Map'), field('level', 'Level'), field('gold', 'Gold'), field('script', 'Script')),
    h('div', { class: 'bars' }, bar('hp'), bar('mp')),
    h('div', { class: 'stats' }, stat('kills', 'Kills'), stat('drops', 'Drops'), stat('quests', 'Quests'), stat('deaths', 'Deaths'), stat('relogins', 'Relogins')),
    r.usage = h('div', { class: 'usage' }),
    h('div', { class: 'actions' },
      r.startStop = h('button', { class: 'small primary', onclick: () => startStop(n) }),
      h('button', { class: 'small', onclick: () => openScriptDialog([n]) }, 'Load…'),
      h('button', { class: 'small', onclick: () => openLog(n) }, 'Log'),
      h('button', { class: 'small', title: 'Show this tab on the VibeSkua desktop', onclick: () => act(`Tab ${n} shown`, () => api('POST', `/api/tabs/${n}/select`)) }, 'Show'),
      h('button', { class: 'small', title: "Restart this tab's Skua (the game stays logged in)", onclick: () => restartTab(n, false) }, 'Restart'),
      h('button', { class: 'small', title: 'Restart Skua and reload the game page (logs in again)', onclick: () => restartTab(n, true) }, 'Reload game'),
      h('button', { class: 'small danger', onclick: () => closeTab(n) }, 'Close')));
  return { el, r, running: false };
}

function updateCard(card, tab, status) {
  const { r } = card;
  const game = status?.game;
  const script = status?.script;
  const stats = status?.stats;
  card.el.classList.toggle('selected', tab.selected);
  r.name.textContent = (game?.loggedIn && game.player) || tab.account || tab.title;

  let pill = ['Not running', 'bad'];
  if (tab.running && !status) pill = ['Starting', 'warn'];
  else if (status && !status.bridgeConnected) pill = ['Game not connected', 'warn'];
  else if (game && !game.loggedIn) pill = [tab.account ? 'Logged out' : 'No account', 'warn'];
  else if (game?.loggedIn) pill = script?.running ? ['Running script', 'ok'] : ['Logged in', 'ok'];
  if (status?.throttle?.headless) pill[0] += ' · headless';
  r.pill.textContent = pill[0];
  r.pill.className = `pill ${pill[1]}`;

  const loggedIn = !!game?.loggedIn;
  r.map.textContent = loggedIn ? `${game.map || '–'}${game.cell ? ` (${game.cell})` : ''}` : '–';
  r.level.textContent = loggedIn ? `${game.level ?? '–'}${game.className ? ` · ${game.className}` : ''}` : '–';
  r.gold.textContent = loggedIn ? fmtNum(game.gold) : '–';
  r.script.textContent = script?.loaded ? `${scriptName(script.loaded)}${script.running ? ' (running)' : ' (loaded)'}` : 'none';
  r.script.title = script?.loaded || '';

  const pct = (a, b) => b ? Math.max(0, Math.min(100, a / b * 100)) : 0;
  r.hpFill.style.width = `${loggedIn ? pct(game.hp, game.maxHp) : 0}%`;
  r.hpText.textContent = loggedIn ? `HP ${fmtNum(game.hp)} / ${fmtNum(game.maxHp)}` : 'HP';
  r.mpFill.style.width = `${loggedIn ? pct(game.mp, game.maxMp) : 0}%`;
  r.mpText.textContent = loggedIn ? `MP ${fmtNum(game.mp)} / ${fmtNum(game.maxMp)}` : 'MP';

  r.kills.textContent = fmtNum(stats?.kills ?? 0);
  r.drops.textContent = fmtNum(stats?.drops ?? 0);
  r.quests.textContent = fmtNum(stats?.questsCompleted ?? 0);
  r.deaths.textContent = fmtNum(stats?.deaths ?? 0);
  r.relogins.textContent = fmtNum(stats?.relogins ?? 0);

  r.usage.replaceChildren(
    h('span', { text: `Skua ${fmtCpu(tab.skua?.cpu)} · ${fmtMb(tab.skua?.memoryMb)}` }),
    h('span', { text: `Game ${fmtCpu(tab.page?.cpu)} · ${fmtMb(tab.page?.memoryMb)}` }),
    h('span', { text: `Restarts ${tab.restarts}` }),
  );

  card.running = !!script?.running;
  r.startStop.textContent = card.running ? 'Stop' : 'Start';
  r.startStop.disabled = !status || (!card.running && !script?.loaded);
  r.startStop.title = !card.running && !script?.loaded ? 'Load a script first' : '';
}

async function startStop(n) {
  const card = cards.get(n);
  if (card.running) await act(`Tab ${n}: script stopped`, () => api('POST', `/api/tabs/${n}/api/script/stop`));
  else await act(`Tab ${n}: script started`, () => api('POST', `/api/tabs/${n}/api/script/start`));
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
    if (btn.dataset.confirm && !confirm(btn.dataset.confirm)) return;
    btn.disabled = true;
    try { await armyAll(btn.textContent, `/api/army/${btn.dataset.army}`); }
    finally { btn.disabled = false; refresh(); }
  });
}

// Every tab's answer: one toast, listing the tabs that failed.
async function armyAll(label, path) {
  try {
    const results = await api('POST', path);
    const failed = Object.entries(results || {}).filter(([, r]) => !r || r.error);
    if (failed.length) toast(`${label}: failed in tab ${failed.map(([t, r]) => `${t} (${r?.error || 'no answer'})`).join(', ')}`, true);
    else toast(`${label}: done in ${Object.keys(results || {}).length} tab(s)`);
  } catch (e) {
    if (e.status !== 401) toast(`${label}: ${e.message}`, true);
  }
}

$('#open-tab').addEventListener('click', async () => {
  await act('Tab opened', () => api('POST', '/api/tabs'));
  refresh();
});

$('#grid-toggle').addEventListener('click', async () => {
  state.grid = !state.grid;
  await act(state.grid ? 'Grid View on' : 'Grid View off', () => api('POST', `/api/grid?on=${state.grid ? 1 : 0}`));
});

$('#load-all').addEventListener('click', () => openScriptDialog(state.tabs.filter(t => t.running).map(t => t.tab)));

$('#jump-all').addEventListener('click', () => {
  const dlg = $('#dlg-jump');
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

$('#options-all').addEventListener('click', () => {
  const list = $('#options-list');
  list.replaceChildren(...OPTIONS.map(([name, label]) => h('div', { class: 'opt' },
    h('span', { text: label }),
    h('button', { type: 'button', class: 'small', onclick: () => armyAll(`${label} on`, `/api/army/option?name=${name}&value=true`) }, 'On'),
    h('button', { type: 'button', class: 'small', onclick: () => armyAll(`${label} off`, `/api/army/option?name=${name}&value=false`) }, 'Off'))));
  $('#dlg-options').showModal();
});

// ---- script dialog --------------------------------------------------------------

let scriptTargets = [];
let searchTimer = null;

function openScriptDialog(tabs) {
  if (!tabs.length) { toast('No running tab to load a script in', true); return; }
  scriptTargets = tabs;
  $('#script-title').textContent = tabs.length === 1 ? `Load script in tab ${tabs[0]}` : `Load script in ${tabs.length} tabs`;
  $('#script-search').value = '';
  $('#script-path').value = '';
  $('#script-list').replaceChildren();
  $('#dlg-script').returnValue = '';
  $('#dlg-script').showModal();
  searchScripts();
  $('#script-search').focus();
}

$('#script-search').addEventListener('input', () => {
  clearTimeout(searchTimer);
  searchTimer = setTimeout(searchScripts, 250);
});

async function searchScripts() {
  const term = $('#script-search').value.trim();
  const list = $('#script-list');
  try {
    const scripts = await api('GET', `/api/tabs/${scriptTargets[0]}/api/scripts?limit=40${term ? `&q=${q(term)}` : ''}`);
    if (!scripts.length) { list.replaceChildren(h('li', { class: 'none', text: 'No scripts found.' })); return; }
    list.replaceChildren(...scripts.map(s => {
      const li = h('li', {}, h('div', { text: s.name || scriptName(s.path) }), h('small', { text: s.description || s.path }));
      li.addEventListener('click', () => {
        for (const x of list.children) x.classList.toggle('active', x === li);
        $('#script-path').value = s.path;
      });
      li.addEventListener('dblclick', () => { $('#script-path').value = s.path; $('#dlg-script').close('load'); });
      return li;
    }));
  } catch (e) {
    if (e.status !== 401) list.replaceChildren(h('li', { class: 'none', text: e.message }));
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

// ---- log dialog -----------------------------------------------------------------

let logTab = null, logSince = 0, logTimer = null;

function openLog(n) {
  logTab = n;
  logSince = 0;
  $('#log-title').textContent = `Tab ${n} log`;
  $('#log-text').textContent = '';
  $('#dlg-log').showModal();
  pollLog();
  logTimer = setInterval(pollLog, 2000);
}

async function pollLog() {
  if (logTab === null) return;
  try {
    const log = await api('GET', `/api/tabs/${logTab}/api/log?type=${$('#log-type').value}&since=${logSince}`);
    const pre = $('#log-text');
    if (log.total < logSince) { pre.textContent = ''; logSince = 0; return; }   // cleared
    if (log.lines.length) {
      pre.append(log.lines.join('\n') + '\n');
      logSince = log.total;
      if ($('#log-follow').checked) pre.scrollTop = pre.scrollHeight;
    }
  } catch { /* try again next time */ }
}

$('#log-type').addEventListener('change', () => { logSince = 0; $('#log-text').textContent = ''; pollLog(); });
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
    h('td', { text: a.user || '' }),
    h('td', {}, h('span', { class: 'pill', text: a.source === 'env' ? 'environment' : 'added here' })),
    h('td', { text: a.server || 'default' }),
    h('td', { text: a.script ? scriptName(a.script) : '–', title: a.script || undefined }),
    h('td', { text: a.autoStart == null ? '–' : a.autoStart ? 'yes' : 'no' }),
    h('td', {}, h('span', {
      class: `pill ${a.loggedIn ? 'ok' : a.open ? 'warn' : ''}`,
      text: a.loggedIn ? 'logged in' : a.open ? 'open, logged out' : 'no tab',
    })),
    h('td', { class: 'actions-cell' }, a.editable ? [
      h('button', { class: 'small', onclick: () => openAccountDialog(a) }, 'Edit'),
      h('button', { class: 'small danger', onclick: () => deleteAccount(a) }, 'Remove'),
    ] : h('span', { class: 'muted small', text: 'set in the environment' })))));
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
  f.server.value = account?.server || '';
  f.script.value = account?.script || '';
  f.autoStart.checked = !!account?.autoStart;
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
  const button = $('#account-save');
  button.disabled = true;
  try {
    const result = await api('PUT', `/api/accounts/${n}`, body);
    f.pass.value = '';
    $('#dlg-account').close('saved');
    toast(`Tab ${n}: ${result.applied}`);
    state.accounts = null;
    refresh();
  } catch (err) {
    $('#account-error').textContent = err.message;
  } finally {
    button.disabled = false;
  }
});

async function deleteAccount(a) {
  if (!confirm(`Remove ${a.user} (tab ${a.tab})? Its tab closes.`)) return;
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
    fraction == null ? null : h('div', { class: 'meter' }, meterFill(fraction)));
  const memLimit = mem.containerLimitMb ?? mem.hostTotalMb;
  $('#res-cards').replaceChildren(
    statCard(`CPU (${r.cpus} cores)`, fmtCpu(r.total?.cpu), (r.total?.cpu || 0) / (r.cpus * 100)),
    statCard(mem.containerLimitMb ? 'Memory (container limit)' : 'Memory (container)', `${fmtMb(mem.containerMb)}${memLimit ? ` / ${fmtMb(memLimit)}` : ''}`,
      memLimit && mem.containerMb ? mem.containerMb / memLimit : null),
    statCard('Host memory free', fmtMb(mem.hostAvailableMb), null),
    statCard('Load (1, 5, 15 min)', (r.load || []).map(v => v.toFixed(2)).join('  ') || '–', null),
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

// Enter in the search box searches now rather than submitting the dialog.
$('#script-search').addEventListener('keydown', e => {
  if (e.key === 'Enter') { e.preventDefault(); clearTimeout(searchTimer); searchScripts(); }
});

// ---- start ----------------------------------------------------------------------

(async () => {
  try { showApp(await api('GET', '/api/session')); } catch { showLogin(); }
})();
