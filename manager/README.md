# VibeSkua Manager

A web page to watch and control a VibeSkua instance from another machine,
like Skua Manager on Windows. It runs in its own small container, next to
VibeSkua or on another host.

- **Bots:** every tab at a glance: character, map, level, gold, HP/MP, the
  script and whether it runs, kills, drops, quests, deaths and relogins, and
  what each tab's Skua and game cost in CPU and memory. Per tab: start/stop the
  script, load a script (search, or browse the Scripts folder), the live script or debug log, show it on
  the VibeSkua desktop, restart it, reload its game, close it.
- **Army:** start/stop all, load a script everywhere (or in the tabs you
  select), log in/out all, jump everyone to a map or player, Skua options for
  every tab, Grid View, open a tab.
- **Accounts:** add, edit and remove accounts while VibeSkua runs. A new
  account opens its tab and logs in, with the Skua options you tick turned on;
  passwords can be set but are never shown again. Accounts set in VibeSkua's
  environment (`AQW_USER_N`) are listed, read-only.
- **Resources:** the container's CPU, memory and load, split by Skua, game
  pages, GPU process, the rest of Electron and the desktop.

It talks to VibeSkua's tab host API from the server, with the API token, so
the browser never sees the token or reaches VibeSkua directly. It has its own
login.

## Using it

Log in with `MANAGER_USER` / `MANAGER_PASSWORD`. The top bar shows whether
VibeSkua answers, its version, uptime and tab count, and the container's CPU,
memory and load. The page refreshes itself every few seconds; there is
nothing to reload.

### Bots

One card per tab:

- **The header:** a box to select the tab (see the Army bar), the tab number
  and the character. Under it, its state (Logged in, Running script, Not logged
  in), with "(headless)" when its Headless Mode is on, and **Shown** on the tab
  the VibeSkua desktop shows now (**In grid** on every tab while its Grid View
  is on).
- **Map, room, level and class, gold, script** (the room needs a VibeSkua newer than
  1.2.0; it reads "hidden" with Streamer mode on), and HP / MP bars.
- **Target:** what the character is fighting, with its HP, and every monster
  in the cell (the dead ones struck through).
- **Quest:** the active quests and each requirement as have / need, so you can
  see a quest filling up.
- **Kills, drops, quests, deaths, relogins** since the session started, and
  what the tab's Skua and game cost in CPU and memory.

Its buttons:

| Button | What |
| :--- | :--- |
| **Start / Stop** | The loaded script. |
| **Load...** | Pick a script: **Search** by name, path, description or tag, in a category, as the Search Scripts window does; or **Browse** the Scripts folder folder by folder (your own and extra repositories, such as `UltrasLW`, included). Click one to fill Path, double-click to load it; **Load & start** starts it too. |
| **Script options...** | The loaded script's options, as its Options window shows them, grouped (the script's, then CoreBots' and the rest). Change them and **Save**; **Defaults** fills in each option's default. Not while the script runs. **Don't open the options window when this script starts** makes it run with the saved options, without asking (see `SKUA_SKIP_SCRIPT_OPTIONS` in [DOCKER.md](../DOCKER.md)). Options that hold a player or account name are hidden like passwords; **Reveal values** shows them. |
| **Skua options...** | This tab's Skua options (Lag Killer, Hide Players, Disable FX, Skip Cutscenes, Infinite Range, Magnetise, Headless Mode, Function-based Skills, Streamer Mode) as checkboxes with their current values. Each change applies right away. |
| **Log in / Log out** | Log this tab's account in, or out (asks first; a running script stops). The button shows whichever applies. |
| **Log** | The live script, debug or Flash log, following new lines. |
| **Show** | Bring the tab to the front on the VibeSkua desktop. |
| **Restart** | Restart the tab's Skua (a running script stops). With the Electron game the game stays logged in; with the native game (`:native` image) the game restarts with it and logs back in by itself. |
| **Reload game** | Restart Skua and reload the game page (it logs in again). |
| **Close** | Close the tab. |

The **Army** bar does the same for every tab at once: start, stop, load a
script, log in or out, **Restart...** (each tab's Skua), jump everyone to a map
or a player, and **Skua options...** (On / Off for each option, since the tabs may differ). **Grid
View** switches the desktop's Grid View, **+ Open tab** opens another tab.

**Some tabs only:** tick the box in the header of the cards you want. The
Army bar is then tinted, its label becomes the selection (**Tabs 2, 4
selected**, with an **x** to clear it), and every button acts on those tabs
only (the ones running): **Start**, **Log out**, **Jump...** and so on, without
"all". The Jump and Skua options dialogs and the Log out question name the
tabs. With no box ticked the buttons act on every tab. The selection stays while
the page is open.

### Accounts

Every tab's account, where it comes from and its state. Accounts set in
VibeSkua's environment (`AQW_USER_N`) are read-only here. **+ Add account**
saves one to `accounts.json` in VibeSkua's config folder: the tab, name,
password, server, a script and whether to start it after logging in. It opens
its tab and logs in right away. A password can be replaced but is never shown
again.

**Open tab:** an account whose tab is closed (status "no tab"), from the
environment or added here, has an **Open tab** button: it opens that tab number
again, and the tab logs the account in.

**Skua options** (when adding, not editing): tick the ones the new tab should
have, such as Lag Killer or Hide Players. They are turned on once, when the tab
first logs in; after that they are the tab's own settings, changed with **Skua
options...** on its card. The manager's server does this, so you can close the
page while the tab starts; it waits up to 15 minutes for the login, and writes
what it turned on in its log.

### Resources

The container's CPU, memory and load; CPU and memory by kind of process
(Skua, game pages, GPU process, the rest of Electron, the desktop); and the
busiest processes. CPU is in percent of one core.

### Streamer mode

The **Streamer mode** switch in the top bar is for showing the page on a
stream or a screenshot. It only changes this page, in this browser:

- tabs are called "Player 1", "Player 2"..., accounts "Account 1"...;
- account and character names, and room numbers, are masked in the logs;
- every text and number option is hidden in Script options;
- the account name field is hidden in the Accounts dialog.

Turning it on also offers to turn on the game's own Streamer Mode in every
tab, which hides the names in the game and on the VibeSkua desktop.

### Tips

- **Timing-heavy scripts** (some Ultras, such as Ultra Speaker) need every tab
  to answer fast. Keep all tabs in Headless Mode while they run, and don't
  keep one open on the desktop: the tab on screen spends its time drawing, and
  every Skua call to it waits.

## Setting it up

VibeSkua needs its tab host API published, with a token
([DOCKER.md](../DOCKER.md#advanced-control-api-and-devtools)):

```yaml
services:
  vibeskua:
    # ... as before, plus:
    environment:
      SKUA_HOST_API_PREFIX: "http://+:8789/"
      SKUA_API_TOKEN: "${SKUA_API_TOKEN}"

  vibeskua-manager:
    image: ghcr.io/jenangmaker/vibeskua-manager:latest
    container_name: vibeskua-manager
    environment:
      MANAGER_USER: "admin"
      MANAGER_PASSWORD: "${MANAGER_PASSWORD}"
      VIBESKUA_URL: "http://vibeskua:8789"     # same compose project: by service name
      VIBESKUA_TOKEN: "${SKUA_API_TOKEN}"
    ports:
      - "192.168.1.10:3040:3040"              # a LAN address
    restart: unless-stopped
```

with an `.env` file next to it:

```
SKUA_API_TOKEN=<a long random string, e.g. openssl rand -hex 32>
MANAGER_PASSWORD=<your login password>
```

Then open `http://192.168.1.10:3040`. In the same compose project VibeSkua's
port 8789 needs no publishing; for a manager on another host, publish it on a
LAN address (`"192.168.1.10:8789:8789"`) and point `VIBESKUA_URL` there.

## Settings

| Variable | Default | What |
| :--- | :--- | :--- |
| `MANAGER_PASSWORD` | (required) | The login password. It does not start without one. |
| `MANAGER_USER` | `admin` | The login name. |
| `VIBESKUA_URL` | `http://127.0.0.1:8789` | VibeSkua's tab host API. |
| `VIBESKUA_TOKEN` | | VibeSkua's `SKUA_API_TOKEN`. |
| `PORT` | `3040` | Where the page is served. |
| `MANAGER_ALLOW` | `lan` | Who may connect: `lan` (private, loopback, link-local and 100.64/10 addresses, which covers Tailscale), `any`, or a comma list of addresses and ranges (`lan,203.0.113.7`). |
| `MANAGER_TRUST_PROXY` | `0` | `1` behind a reverse proxy: take the client address and https from `X-Forwarded-For` / `X-Forwarded-Proto`. |
| `MANAGER_SECURE_COOKIE` | `0` | `1`: the session cookie is only sent over https. Automatic behind a proxy that says https. |
| `MANAGER_SESSION_HOURS` | `12` | How long a login lasts. |

## Security

Whoever logs in controls every account in the instance, so:

- **Keep it on your LAN** (the default refuses other addresses), or reach it
  through a VPN such as Tailscale. Behind a reverse proxy on the internet you
  must set `MANAGER_ALLOW=any` and `MANAGER_TRUST_PROXY=1`: then the password
  is all that stands in the way, so make it long and serve it over https only.
- **Logins:** five wrong passwords lock that address out for 15 minutes.
  Sessions are kept in memory: restarting the manager logs everyone out.
- **The page:** a strict Content-Security-Policy, no third-party code, an
  HttpOnly SameSite=Strict session cookie, and every API call must carry a
  header other sites cannot send.
- **The log** (`docker logs vibeskua-manager`) records logins, failed logins
  and every action taken (method and path, never request bodies, which carry
  passwords).

## Running it without Docker

Node 22 or newer, no packages to install:

```bash
cd manager
MANAGER_PASSWORD=... VIBESKUA_URL=http://192.168.1.10:8789 VIBESKUA_TOKEN=... node server.js
```
