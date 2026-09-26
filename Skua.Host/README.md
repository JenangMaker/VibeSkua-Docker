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

## Control API

Local only (`SKUA_API_PREFIX`, default `http://127.0.0.1:8791/`). There is no
authentication: whatever can reach it can run code as the bot.

```sh
curl -s localhost:8791/status
curl -s -X POST -d '' 'localhost:8791/script/start?path=/path/to/Script.cs'
curl -s -X POST --data-binary @Script.cs localhost:8791/script/start
curl -s -X POST -d '' localhost:8791/script/stop
curl -s 'localhost:8791/log?type=script&since=0'     # script | debug | flash
```

Always send a body with POST (`-d ''`): .NET's HttpListener on Linux answers a
body-less POST with 411 but may still run the handler.

`SKUA_SCRIPT=/path/to/Script.cs` (or `--script`) starts a script once the page
is connected and logged in.

## Status (2026-09-26)

Built and run on Linux (.NET 10) against a logged-in session: `Skua.Core`
reads player, map, cell and HP through the bridge, and a script compiled by
Roslyn on Linux joined battleontown and killed three Frogzards with the skill
rotation running (`Kill.Monster`, `Skills.Start`).
