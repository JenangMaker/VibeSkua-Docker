# Skua.Host

Skua without a UI, for Linux. `Skua.Core` - scripts, combat, skills, quests,
options - driving `skua.swf` in the vibeskua-web container's Ruffle page
through `Skua.Ruffle`'s WebSocket bridge, in place of the WPF app and the Flash
ActiveX control.

- The page connects to the bridge (`SKUA_BRIDGE_PREFIX`, default
  `http://127.0.0.1:8790/`); only the page's origin is accepted.
- Windows, dialogs, file pickers, hotkeys and the like are headless stand-ins
  (`HeadlessServices.cs`); anything that would ask the user takes the
  cautious answer (No / Cancel) and logs it.
- Settings are Skua's own (`UnifiedSettingsService`), in the same files.

## In the container

The runtime is in `Skua.Linux`. The image ships `Skua.App.Avalonia`, which is
the same runtime with Skua's windows, and runs without them under
`--headless` / `SKUA_UI=0`; this project stays as the minimal headless build.
`docker/Dockerfile.kasm` publishes it self-contained to `/opt/skua` (not
single-file: the script compiler needs the assemblies on disk), and
`web/main.js` starts it when `SKUA_HOST=1`; the page then connects on its own.
See `web/README.md` ("Skua").

## Control API

Local only (`SKUA_API_PREFIX`, default `http://127.0.0.1:8791/`). With
`SKUA_API_TOKEN` set, every request must carry it, as
`Authorization: Bearer <token>` or `X-Api-Token: <token>`; without it there is
no authentication, and whatever can reach the port can run code as the bot.

```sh
curl -s localhost:8791/status
curl -s -X POST -d '' 'localhost:8791/script/start?path=/path/to/Script.cs'
curl -s -X POST --data-binary @Script.cs localhost:8791/script/start
curl -s -X POST -d '' 'localhost:8791/script/load?path=Farm/GoldFarm'   # load only
curl -s -X POST -d '' localhost:8791/script/start    # start the loaded script
curl -s -X POST -d '' localhost:8791/script/stop
curl -s 'localhost:8791/log?type=script&since=0'     # script | debug | flash
```

Always send a body with POST (`-d ''`): .NET's HttpListener on Linux answers a
body-less POST with 411 but may still run the handler.

| Route | What |
| :--- | :--- |
| `GET /status` | Connected, logged in, player, map, cell, HP, the script and whether it runs. `?detail=1` adds level, gold, class, MP, the session stats, the target and the cell's monsters (`combat`), and the active quests with their requirements (`quests`). |
| `POST /script/load?path=` | Load a script (a path relative to `Skua/Scripts`, or absolute) without starting it. |
| `POST /script/start[?path=]` | Start the loaded script, or load and start `path`, or the script sent as the body. |
| `POST /script/stop` | Stop the script. |
| `GET /script/options` | The loaded script's options (as its Options window shows them), each with its type, choices, value and default, and whether its options window is skipped at start. |
| `POST /script/options` | Body `{"values":[{"category","name","value"}], "skipWindow": bool}`: save option values (refused while the script runs) and/or set "don't open the options window at start" for this script. |
| `GET /log?type=script|debug|flash[&since=]` | The log, from line `since`. |
| `GET /scripts?q=&category=&limit=` | Search scripts as the Search Scripts window does: name, path, description or tag, in a category, sorted by name. |
| `GET /scripts/categories` | The categories `/scripts` takes. |
| `GET /scripts/browse?dir=` | One folder of `Skua/Scripts` on disk: its subfolders (with script counts) and `.cs` files, named from the index or the file's header. |
| `POST /scripts/update` | Sync the script repositories again. |
| `POST /scripts/reset` | Delete all scripts and download them afresh (see below). |
| `GET /army/options` | The Skua options Army Control sets (Lag Killer, Hide Players, Headless Mode...), as `{name: true|false}`. |
| `POST /debug/trace?secs=` | CPU-sample this Skua process for `secs` seconds (5-300, default 30), as `dotnet-trace`'s cpu-sampling profile does, into `Skua/traces` in the config folder. Open it with PerfView, or `dotnet-trace convert --format Speedscope`. |
| `POST /army/<command>` | An Army Control command for this Skua: `login`, `logout`, `jump?map=&cell=`, `goto?player=`, `option?name=&value=`, `start`, `stop` and more, listed in [ArmyApi.cs](../Skua.Linux/ArmyApi.cs). |

`SKUA_SCRIPT=/path/to/Script.cs` loads a script at startup (in the UI it shows
in the Script Loader, ready to start); `SKUA_SCRIPT_AUTO_START=1` also starts
it once the page is connected and logged in. `--script` loads and starts.

## Scripts

At startup the host syncs Skua's script repository (auqw/Scripts, branch
Skua) into `Skua/Scripts`, the way the WPF app does: it downloads missing and
outdated scripts (all ~1900 take under a minute the first time) and refreshes
the advanced skill sets, quest data and junk list. The same settings apply
(`CheckBotScriptsUpdates`, `AutoUpdateBotScripts`, ...).

So a script can be named by its repository path, relative to `Skua/Scripts`,
case-insensitively, with or without `.cs`:

```sh
curl -s 'localhost:8791/scripts?q=gold farm'                 # search: path, name, tags
curl -s -X POST -d '' 'localhost:8791/script/start?path=Farm/GoldFarm'
curl -s -X POST -d '' localhost:8791/scripts/update            # sync again
curl -s -X POST -d '' localhost:8791/scripts/reset             # delete ALL scripts, download afresh
```

`/scripts/reset` is Skua Manager's "Reset Scripts": it empties the Scripts
folder (scripts of your own included), then downloads everything again. It
refuses while a script is running.

`SKUA_SCRIPT=Farm/GoldFarm` works the same way.

Community scripts include `CoreBots.cs`, which has `using System.Windows.Forms;`
and one WinForms dialog. Linux has no WinForms, so the host ships a stand-in
(`Skua.Host.WinForms`, assembly `System.Windows.Forms`) with just the members
CoreBots uses: scripts compile, and showing a form throws. On Linux the script
compiler also references the whole framework (`Skua.Core/AppStartup/Services.cs`);
on Windows the loaded assemblies already covered it.

## Status (2026-09-26)

Built and run on Linux (.NET 10) against a logged-in session: `Skua.Core`
reads player, map, cell and HP through the bridge, and a script compiled by
Roslyn on Linux joined battleontown and killed three Frogzards with the skill
rotation running (`Kill.Monster`, `Skills.Start`). The self-contained
linux-x64 publish the image uses ran the same script.
