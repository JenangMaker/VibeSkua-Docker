# VibeSkua Manager

A web page to watch and control a VibeSkua instance from another machine,
like Skua Manager on Windows. It runs in its own small container, next to
VibeSkua or on another host.

- **Bots:** every tab at a glance: character, map, level, gold, HP/MP, the
  script and whether it runs, kills, drops, quests, deaths and relogins, and
  what each tab's Skua and game cost in CPU and memory. Per tab: start/stop the
  script, load a script (search, or browse the Scripts folder), the live script or debug log, show it on
  the VibeSkua desktop, restart it, reload its game, close it.
- **Army:** start/stop all, load a script everywhere, log in/out all, jump
  everyone to a map or player, the Misc Options, Grid View, open a tab.
- **Accounts:** add, edit and remove accounts while VibeSkua runs. A new
  account opens its tab and logs in; passwords can be set but are never shown
  again. Accounts set in VibeSkua's environment (`AQW_USER_N`) are listed,
  read-only.
- **Resources:** the container's CPU, memory and load, split by Skua, game
  pages, GPU process, the rest of Electron and the desktop.

It talks to VibeSkua's tab host API from the server, with the API token, so
the browser never sees the token or reaches VibeSkua directly. It has its own
login.

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
