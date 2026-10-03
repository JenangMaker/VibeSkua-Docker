# VibeSkua for Docker
> **Note:** This project is **Vibe Coded**—built through AI-assisted development, and pure momentum.

Run [VibeSkua](https://github.com/NinjaXz/VibeSkua), the multi-account fork of
[auqw/skua](https://github.com/auqw/skua), on a Linux server and use it from any
browser. No Windows, no Flash install, no VM: one container with Skua's full UI,
the game embedded under its menu, and every account in its own tab.

The Windows app is still here and still builds as before; this fork adds a
Linux edition next to it.

## Quick start

```yaml
# docker-compose.yml
services:
  vibeskua:
    image: ghcr.io/jenangmaker/vibeskua-web:latest
    ports:
      - "3000:3000"
    environment:
      PUID: "1000"            # `id -u` of the user owning ./config
      PGID: "1000"
      CUSTOM_USER: "vibeskua" # login for the web desktop
      PASSWORD: "change-me"
      # AQW_USER_1: "first account"   # optional: log in automatically,
      # AQW_PASS_1: "..."             # one tab per account
    volumes:
      - ./config:/config      # settings, scripts, CoreBots options
    # devices:                # a GPU (Intel/AMD) makes the game smooth
    #   - /dev/dri:/dev/dri
    shm_size: 1gb
    security_opt:
      - seccomp=unconfined
    restart: unless-stopped
```

```bash
docker compose up -d
# then open http://<host>:3000
```

[DOCKER.md](DOCKER.md) is the full guide: several accounts, scripts, settings,
performance, every environment variable and troubleshooting. A commented
[docker-compose.minimal.yml](docker-compose.minimal.yml) is ready to copy.

> Keep port 3000 on your LAN or behind a reverse proxy with HTTPS: whoever
> reaches it controls the bot and the logged-in accounts.

Never used Docker? Follow [the step-by-step guide](#new-to-docker-step-by-step) below.

## New to Docker? Step by step

Docker runs VibeSkua in a sealed box (a *container*) with everything it needs
already inside: you don't install Skua, .NET, a browser or Flash yourself. You
download the box, start it, and open it in your web browser.

### 1. What you need

- **A computer that stays on while the bots run.** It must have an Intel or AMD
  64-bit processor (most PCs, servers and mini PCs). Raspberry Pi and Apple Silicon
  Macs are not supported.
  - **A Linux PC or server is best.** Ubuntu and Debian work well.
  - **Windows 10/11 works too,** through Docker Desktop. The game is then drawn
    on the CPU, so it is slower and heavier.
- **Memory:** about 1 GB plus about 1 GB per account you run at once.
- **Optional, for smooth play:** an Intel or AMD graphics chip. On Linux, the
  command `ls /dev/dri` shows `renderD128` if you have one.

### 2. Install Docker

**Linux (Ubuntu, Debian and most others).** Open a terminal and run:

```bash
curl -fsSL https://get.docker.com | sudo sh
sudo usermod -aG docker $USER
```

Log out and back in, so the second line takes effect. Then check that it works:

```bash
docker run --rm hello-world
```

**Windows.** Install [Docker Desktop](https://www.docker.com/products/docker-desktop/),
keep the default *Use WSL 2* option, and restart when it asks. Start Docker
Desktop and wait until it says it is running. Type the commands below in
**PowerShell**.

### 3. Make a folder with two files

Make a folder for VibeSkua, for example `vibeskua` in your home folder. Everything
VibeSkua saves (settings, scripts, CoreBots options) will be kept in it.

**First file: `docker-compose.yml`.** It describes the container. Copy this into it:

```yaml
services:
  vibeskua:
    image: ghcr.io/jenangmaker/vibeskua-web:latest
    container_name: vibeskua
    ports:
      - "3000:3000"
    env_file: .env
    volumes:
      - ./config:/config
    # Linux with a graphics chip: remove the # from the next two lines.
    # devices:
    #   - /dev/dri:/dev/dri
    shm_size: 1gb
    security_opt:
      - seccomp=unconfined
    restart: unless-stopped
```

**Second file: `.env`, with the dot at the start.** It holds your passwords, so
they are not in the first file. Put your own values after each `=`, without
quotes:

```
# Login for the VibeSkua page in your browser
CUSTOM_USER=vibeskua
PASSWORD=pick-a-password

# Linux only: your user's numbers, from the commands `id -u` and `id -g`
PUID=1000
PGID=1000

# Your time zone, e.g. Asia/Jakarta or Europe/London
TZ=Etc/UTC

# Optional: accounts that log in by themselves, one tab each
AQW_USER_1=
AQW_PASS_1=
AQW_USER_2=
AQW_PASS_2=
```

You can leave the accounts empty, and log in from inside Skua instead.

On Windows, Notepad may save the file as `.env.txt`. In the save dialog, choose
*All files* and type the name as `.env`.

### 4. Start it

In a terminal, go to the folder (`cd vibeskua`) and run:

```bash
docker compose up -d
```

The first time, this downloads VibeSkua, which is a large download and takes a
few minutes. After that it starts in seconds. It also starts by itself when the
computer restarts (`restart: unless-stopped`).

### 5. Open it

- **On the same computer:** open <http://localhost:3000> in your browser.
- **From another device on your network** (a laptop or phone): use the
  computer's address, such as `http://192.168.1.10:3000`. Find the address with
  `hostname -I` on Linux, or `ipconfig` on Windows (the *IPv4 Address* line).

Log in with `CUSTOM_USER` and `PASSWORD` from your `.env`. You will see Skua
with the game under its menu, one tab per account. On the first start Skua
fetches the bot scripts; give it a minute, and choose to update them if it asks.

### 6. Everyday commands

Run these from inside the folder.

| To... | Run |
| :--- | :--- |
| Stop it | `docker compose stop` |
| Start it again | `docker compose start` |
| See what it is doing (Ctrl+C to quit) | `docker compose logs -f --tail 100` |
| Apply changes to `.env` or `docker-compose.yml` | `docker compose up -d` |
| Update to the newest VibeSkua | `docker compose pull` then `docker compose up -d` |
| Remove the container (your `config` folder is kept) | `docker compose down` |

### 7. If something goes wrong

- **"permission denied" when running `docker` on Linux:** you did not log
  out and back in after installing. Or put `sudo` in front of the command.
- **"port is already allocated":** something else uses port 3000. Change
  `"3000:3000"` to `"3100:3000"` and open port 3100 instead.
- **The page does not open from another device:**
  - Check the address.
  - Check that both devices are on the same network.
  - On Linux with a firewall, allow your network, for example
    `sudo ufw allow from 192.168.0.0/16 to any port 3000`.
- **The game is slow or choppy:** give it a graphics chip (step 3). Run fewer
  accounts, or turn on *Headless Mode* for the ones you don't watch.
- **It keeps restarting, or the computer runs out of memory:** run fewer
  accounts, or add memory. Check with `docker stats`.
- **Anything else:** run `docker compose logs --tail 200` and look for the
  first error. [DOCKER.md](DOCKER.md#troubleshooting) has more.

> **Keep it private.** Do not forward port 3000 on your router. Anyone who can
> open the page controls your bots and the accounts logged into them. To use it
> away from home, use a VPN such as [Tailscale](https://tailscale.com).

## What you get

- **The whole Skua UI:** every screen of the Windows client (scripts, options,
  CoreBots options, skills, loadouts, packets, plugins, hotkeys, ...), with the
  game embedded under the menu.
- **Several accounts in one window:** one tab per account, with **Army Control**
  (log in/out, jump, start/stop scripts, the Army Scheduler across all tabs) and
  **Grid View** to watch them all at once.
- **Unattended by design:**
  - log accounts in from environment variables (`AQW_USER_<N>`, `AQW_PASS_<N>`,
    `AQW_SERVER`);
  - load and start a script per account (`SKUA_SCRIPT`, `SKUA_SCRIPT_AUTO_START`);
  - one private room for every account's CoreBots scripts (`SKUA_ROOM_NUMBER`);
  - log back in after a disconnect, letting Skua's own relogin go first;
  - reload the game client every so often to free the memory Ruffle keeps
    (`RECYCLE_AFTER_MINUTES`).
- **Scripts kept up to date:** synced from
  [auqw/Scripts](https://github.com/auqw/Scripts) (or your own repository) at
  startup, with a progress indicator, a popup if files fail, and **Scripts >
  Reset Scripts...** like Skua Manager's. Auto-started scripts wait until the
  sync has finished writing.
- **Light on the host:** tabs you are not looking at run at 2 fps with their game
  drawn at 1x1, Headless Mode does the same for the one you are, and a bot
  dashboard (kills, drops, quests, deaths, relogins) sits beside the game.
- **Your data in one folder:** mount `/config` and settings, scripts, CoreBots
  options and plugins survive updates.
- **A web manager:** [VibeSkua Manager](manager/README.md), a separate small
  container, lets you watch and control every bot from a phone or another PC
  without the remote desktop: status, stats, target and quest progress per
  account; script and Army controls, script search and folder browsing, script
  options and Skua options per tab; logs, adding accounts while it runs, the
  container's resources, and a streamer mode. See [manager/README.md](manager/README.md).

## How it works

```
browser ── KasmVNC (port 3000) ── a small Linux desktop in the container
                                    ├─ Skua (Avalonia port of the WPF UI, one process per tab)
                                    │     └─ Skua.Core, unchanged scripts API
                                    └─ Electron ── Ruffle (Flash player, WebAssembly) ── AQW
                                          the game window is embedded under Skua's menu
```

- **Skua.App.Avalonia** is the Windows client's WPF UI ported to
  [Avalonia](https://avaloniaui.net), on the same Skua.Core, so scripts run as
  they do on Windows.
- **The game** runs in [Ruffle](https://ruffle.rs) inside Electron. It uses a
  fork with fixes for AQW (loader, canvas rendering, speed without a GPU):
  [JenangMaker/ruffle](https://github.com/JenangMaker/ruffle), branch
  `aqw-loader-fixes`.
- **Skua.Linux** connects the two (a WebSocket bridge in place of Flash's COM
  interface) and adds the container's extras: script sync, a control API, tab
  throttling.

## Differences from the Windows app

- **Ruffle is not Flash.** The game plays and scripts run, but a few effects are
  missing with the default `webgl` renderer (skill cooldown darkening, aura
  fades). Where Ruffle behaves differently in ways scripts notice (for example
  the order of a quest's requirements), Skua.Core corrects for it.
- **Without a GPU** the game is drawn on the CPU: it works, but slowly and at a
  CPU cost. Pass `/dev/dri` through if the host has an Intel or AMD GPU.
- **Not included:** Skua Manager (accounts come from environment variables,
  or from the web manager instead), the Daily Tracker plugin (Windows only), and the in-game quest wiki
  links and drop rates (switched off: they error constantly under Ruffle).

## Building

- **The Docker image:** see [Building the image yourself](DOCKER.md#building-the-image-yourself).
  Pushing a `v*` tag, or running the **Publish image** workflow, publishes
  `ghcr.io/jenangmaker/vibeskua-web` from GitHub Actions.
- **The Windows app,** as upstream:
  1. **Automated:** run **BuildRelease.bat** in the root folder. The output lands
     in a new **Build** folder.
  2. **Manual:** from the root folder run
     ```bash
     dotnet build Skua.sln -c Release -p:WarningLevel=0 --nologo
     ```
  3. **In Docker,** without the .NET SDK or Visual Studio: `docker compose run
     --rm build`; the output still lands in **Build/AnyCPU**. See
     [docs/BUILD-DOCKER.md](docs/BUILD-DOCKER.md).

## VibeSkua features

Everything VibeSkua adds over auqw/skua, as its README describes it (these are
features of the Windows app; most carry over to the Docker edition through
Skua.Core).

<details>
<summary>Quality of life & features</summary>

| Feature | Original | This Fork |
| :--- | :--- | :--- |
| **Discord Integration** | Lacked native capability. | `DiscordWebhookService` integrated natively. Features rich visual embed cards (`Script Started`, `Farming Session Concluded`, `Scheduler Paused`), automatic rate-limiting (`HTTP 429`) retry loops, a threaded queue structure to prevent dropped packets, and `CachedUsername` preservation so webhooks and script status alerts always display your character's real username after disconnections. |
| **Headless Mode** | Full-screen rendering; high resource demand per instance. | Introduced a 1x1 hidden pixel viewport, forcing Flash to bypass geometry/blitting and significantly reducing resource consumption. |
| **Script Scheduling** | Required manual initialization and supervision with static script options. | Added autonomous script queuing with independent option profiles, custom display names, save/load playlist states, `SilentConfig` unattended execution (`popup modal windows automatically suppressed so overnight farming never stalls`), smart auto-relogin collision protection (`resumption recognition prevents skipped items when reconnecting during playlists`), plain-English error unwrapping, and automatic paused state monitoring (`Scheduler Paused`). |
| **Account Tabs** | Required running individual instances which clutters the screen. | Embedded `EmbeddedMainWindow.xaml` with dynamic SWF patching for a unified, tabbed WPF interface. Features instantaneous multi-account closing (`asynchronous background process killing shuts down 7+ tabs in milliseconds without UI freezing`). |
| **Script Sorting** | Basic navigation options. | Expanded `ScriptRepoViewModel.cs` to support dynamic sorting by Name, Date, or script category (Ascending/Descending). |
| **Pause Functionality** | Could only fully Stop scripts, entirely losing current progression. | Built a native `Pause` feature that safely freezes the execution thread in place, letting you interact with menus and resume later. |
| **Smart Grid View** | Required managing dozens of overlapping individual windows. | Consolidates all active accounts into a clean, clutter-free grid inside a single window to monitor a full army at once. |
| **Instance Dashboard** | Lacked a native farming statistics dashboard. | Pinned a native Side Dashboard directly to the game frame to track Kills, Drops, and Quests at a glance. |
| **Function Based Skills** | Relied on static, hardcoded skill sequences without situational awareness. | Integrated a conditional combat engine (`ISkillProvider`) that evaluates health, cooldowns, and missing auras natively via C# before casting. Features a smart two-step survival priority check (`emergency heals/shields evaluated before attack weaving`), automatic high-damage boss recognition (`7-second encounter checks switch to defensive stances on heavy burst damage`), dynamic mid-script class adaptation (`instant combat routine swapping`), an active Action Bar UI Scavenger (`30 FPS and readiness checks force-clear stuck green cooldown spinners`), and adaptive routines for all end-game classes (`Void Highlord, ArchMage, Chrono Assassin, Legion Revenant, Chaos Avenger, etc.`). |
| **Streamer Mode** | Basic privacy capabilities. | Actively scrubs character names, guild tags, room numbers, and disables chat via background asynchronous Flash injection. Fixed OBS/Discord screen share capture to prevent blank/grey screens. |
| **Auto-Relogin Resilience** | Basic relogin handling prone to freezing during network timeouts. | Redesigned with asynchronous task scheduling, dynamic alternative server selection, and fallback socket injection. Upgraded with staggered multi-account batch launches (`500ms–900ms stagger paired with retry jitter to prevent server rate-limiting`), an active `"Stuck on Login"` monitor (`automatic UI reset and rescue if authentication hangs above 5 seconds`), and synchronized character data buffering (`waiting for server list readiness before joining to completely eliminate "Character Data Could Not Be Loaded" errors`). |
| **Army Control & Navigation** | Required managing each client independently. | Features a Centralized Playlist Orchestrator to broadcast schedules, "Load Script to All", strict login validation, separate Map and Cell input boxes for exact room placement (`teleport across rooms inside your current map without reloading`), exact spawn/pad targeting (`e.g. Boss, Left`), a dedicated `"Jump All to Player..."` option (`instant 1-click /goto commands across all tabs`), and a robust IPC system ensuring synchronized execution to the exact millisecond. |
| **Smart Quest Sync & Updater** | Out-of-sync local files requiring external updater scripts and full loops. | Dual-folder `QuestData.json` synchronization (`AppData\Roaming\Skua` and `Scripts` folders stay automatically synced). Built-in Smart Incremental Updater (`Rebuild`, `Update +100 Buffer`, `Range` modes) checks IDs rapidly without triggering GitHub rate limits or freezing the client, automatically discarding invalid or removed quests. |
| **Map & Room Loading Reliability** | Hardcoded short timeouts causing scripts to drop commands or freeze during black loading screens. | Aligned loop thresholds with exact timeout math (`6.0s map load and 3.0s action ceilings`). Characters wait cleanly for Flash multi-client handshakes and always jump directly to the intended destination room (`e.g. Boss, Left`) without dropping into entrance cells (`m1, Left`) or going AFK across room boundaries. Features smart spawn recovery after death and instant script stopping during respawn delays. |
| **Reorganized Navigation & UI** | Cluttered or unorganized top menu navigation. | Reorganized left-to-right based on workflow priority (`Scripts`, `Options`, `Tools & Helpers`, `Combat`, `Bank`, `Diagnostics`). Features instant batch-loading for the Daily Tracker window, categorized daily vs. weekly ultra boss lockouts, and a unified developer diagnostics workspace (`Logs, Console, Spammer, Logger, Interceptor`). |
| **Loadouts Manager** | Non-existent or fully manual. | Fully automates item equipping and dynamic (Forge) enhancements natively via C#. Features a smart banking algorithm, missing gear alerts, and state restoration. |
| **Custom Scripts Loader** | Only supported official repository scripts. | Natively load individual local scripts or custom script directories directly into the UI, complete with safe file deletion prompts and ghost entry cleanup. |
| **Wiki Integration** | Required manual searching on a browser. | Directly click on item requirements within the Quest UI to instantly redirect to the corresponding AQW Wiki page. |
| **Custom Hotkeys** | Relied on static, hardcoded keyboard shortcuts. | Replaced static keybinds with a dynamic `IHotKeyService` leveraging `NHotkey.Wpf`. Integrates natively with `ISettingsService` to allow full user customization of core application commands across the entire WPF interface. |

</details>

<details>
<summary>Performance & engine optimizations</summary>

* **Combat Cooldown Deadlock & Race Elimination:** Reworked Global Cooldown (`GCD`) index checks in `AdvancedSkillCommand.cs` by verifying skill readiness (`isOK` and Flash `canUseSkill`) before advancing rotation indices, completely eliminating endless Auto-Attack loops (`Only using autoattack`). Enforced a 3.5-second bounded safety ceiling in `ScriptSkill.cs` (`Wait.ForTrue`) so background threads break cleanly and trigger `OnTargetReset()` during monster death or UI lockups without deadlocking or freezing your character.
* **ActionScript 3 (SWF) Garbage Collection & Animation Protection:** Throttled Flash display tree inspection modules (`DisableFX` and `HidePlayers`) down to synchronized 2 FPS checks, cutting string allocations and Flash garbage collection overhead by over 93% to prevent overnight out-of-memory crashes. Disabled destructive animation clipping (`OptimizePlayers`) so room and character poses keep playing smoothly during map transitions, and added strict null safety checks in `RemoteRegistry.as` (`destroy()` and `ext_destroy()`) to eliminate Flash `Error #1009` crashes during C# COM object cleanups.
* **C# / .NET Core Concurrency & Memory Upgrades:** Added automatic Large Object Heap cache clearing for dynamic XML game queries (`getGameObject`), fast multi-file `#include` dependency preprocessing, accurate `ScriptWait` loop math (`dividing timeout arguments by 100 instead of 1000`), and thread-safe COM dispatching across UI boundaries while keeping background script threads thread-safe.
* **Multi-Account Instant Teardown:** Replaced sequential COM handshakes on the UI thread with asynchronous background process killing (`TabbedHostWindow.xaml.cs`), allowing instantaneous 1-millisecond window closures and parallel `Skua.exe` termination for 7+ active tabs without UI freezing.
* **SWF Memory Caching:** Implemented `PreloadSwf()` in `FlashUtil.cs` to cache files directly in RAM, accelerating instance launches. Enforced `WMode="direct"` for hardware-accelerated rendering.
* **Optimized Memory Management:** Mitigated memory leaks in the WPF client by properly detaching from `StrongReferenceMessenger` and correctly managing `WeakEventManager` hooks, preventing slowdowns over long sessions.
* **Fluid Asynchronous Operations:** Replaced blocking `Thread.Sleep` calls with non-blocking `await Task.Delay` across automated hunting loops, freeing up thread-pool resources and reducing UI micro-stutters.
* **Clean Task Lifecycles & UI Decoupling:** Overhauled background processes (like `DailyTracker` and `SingleInstanceWatcher`) to respect cancellation tokens and fully decouple heavy game-state checks from the main rendering thread.
* **Network Proxy Optimization:** Refactored `CaptureProxy.cs` to utilize `Encoding.UTF8.GetBytes()` for packet conversion, minimizing latency during high-traffic sessions.
* **GitHub Script Caching Engine:** Engineered `ScriptDates.json` to store metadata and track SHA hashes. Intelligent API querying conserves rate limits and provides graceful UI fallbacks on connection failure.
* **Background Connection Stability:** Repositions inactive clients off-screen and uses a `WPF DispatcherTimer` to ping the `isLoggedIn` COM interface every 500ms, preventing OS-level socket throttling.
* **Active Memory Management:** Introduced `MemoryUtils.cs` to periodically trim the application’s working set, ensuring RAM stability during long, multi-day farming sessions.
* **Asynchronous Flash Injection:** Built a background loop to actively override ActionScript 3 variables (e.g., `world.strMapName`) every 500ms to maintain privacy in Streamer Mode.
* **Release Portability:** Updated plugins like Daily Tracker with PostBuild MSBuild targets to automatically compile and bundle into the release folder during `BuildRelease.bat`.
* **Velopack Deployment Architecture:** Fully migrated the deployment infrastructure to Velopack. Enables rapid silent installations, automatic desktop shortcut provisioning, and a built-in Updater Tab within the Manager for background auto-updating via the GitHub Releases API.
* **And Many More:** Dozens of underlying architectural, thread-safety, and runtime stability enhancements across the entire framework.

</details>

### Copyright & Disclaimer

**Educational & Personal Use Only:** This project is a derivative of [auqw/skua](https://github.com/auqw/skua) (through [NinjaXz/VibeSkua](https://github.com/NinjaXz/VibeSkua)) and is provided "as-is" under the MIT License. The Docker edition bundles [Ruffle](https://ruffle.rs) (MIT / Apache-2.0). I do not claim ownership of the original assets, game data, or the intellectual property of the game developers.

**Disclaimer:** Use of this software may violate the Terms of Service of the associated game. The author assumes no responsibility for any account actions, bans, or other consequences taken by game developers against users of this software. By using this tool, you acknowledge that you do so entirely at your own risk. If your PC decides to commit a toaster bath, that is not my problem.
