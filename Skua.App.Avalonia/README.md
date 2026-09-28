# Skua.App.Avalonia

The VibeSkua client UI (Skua.App.WPF + Skua.WPF) ported to Avalonia 11, for
Linux. It runs Skua.Linux's runtime (Skua.Core driving `skua.swf` in the
vibeskua-web Ruffle page through Skua.Ruffle's bridge) under Skua's own
windows: the main menu, the bot window and every managed window.

In the container, `web/main.js` starts it when `SKUA_HOST=1`; its windows
appear on the KasmVNC desktop next to the game. `SKUA_UI=0` (or no `DISPLAY`)
runs the same binary without windows, as Skua.Host did. The control API and
script sync are the same in both (see `Skua.Host/README.md`).

```sh
Skua.App.Avalonia                  # windows + bot + control API
Skua.App.Avalonia --headless       # bot + control API only
Skua.App.Avalonia --snapshot DIR [filter]   # render views to DIR/*.png and exit
```

## How the port maps

| WPF | Here |
| :- | :- |
| `XAML/DataTemplates.xaml` | `ViewRegistry.cs` + `ViewLocator.cs` (a view model's nearest registered base class wins; unported ones show "Not ported yet") |
| `Services/*` (dialogs, windows, file dialogs, clipboard, dispatcher, hotkeys, themes) | `Services/*` - synchronous like WPF's: on the UI thread a nested loop runs until a dialog closes |
| `HostWindow` / `HostDialog` / `BotWindow` / `MainWindow` | same names; `MainWindow` is the menu and a status line over the game, as in WPF: the game's Electron window is re-parented into it (`GameEmbed.cs`; X11's SetParent). Other windows open at their size, centred (`WindowPlacement.cs`: the KasmVNC desktop maximizes every window) |
| Behaviours | `Behaviors.cs` attached properties (`b:Behave.OnlyNumbers`, `CopySelected`, `ScrollToEnd`, `DeleteSelected`, ...) |
| `ICollectionView` filters | `UiMirror<T>`: a filtered UI-thread copy (the script list changes on other threads) |
| `ListBox.SelectedItems` as a command parameter | `Selection.Run(command, list)` in code-behind (Avalonia's is not an `IList<object>`) |
| `PropertyGrid` (grabber) | `PropertyInspector`: read-only property list |
| MaterialDesign theme + palette editor | Fluent in Skua's colours; `AvaloniaThemeService` reads/writes the same `CurrentTheme`/`UserThemes` settings (base theme and colours; MaterialDesign's contrast adjustment has no counterpart) |
| MdXaml | Markdown.Avalonia.Tight |

Every view of the client is ported. Not ported: the Flash host windows
(`TabbedHostWindow`, `EmbeddedMainWindow`, `GameContainerUserControl`; the
game is embedded by `GameEmbed` instead), the tray icon and balloon tips, and the Skua.Manager
views (account manager, launcher, updaters, groups), which belong to a
separate app.

The game gets keyboard focus while the pointer is over it (the window
manager only focuses Skua's window). If Skua stops, the game window is handed
back to the desktop; if Skua is killed outright, main.js opens a new one,
which logs back in. `--fake-game` stands in for the game window when testing
the embedding without the container.

Hotkeys work while a Skua window has focus, as in WPF (they are key
bindings on the main window, not global).

## Script sync

At startup Skua syncs its Scripts folder with the scripts repository
(`Skua.Linux/ScriptSync.cs`): `SKUA_SCRIPTS_REPO` / `SKUA_SCRIPTS_BRANCH`,
a GitHub or Gitea repository (default auqw/Scripts, branch Skua).
Script dates are only fetched from GitHub. With Options > Application > Auto Update
Scripts on, it downloads missing and outdated scripts silently, as the WPF
app does; otherwise it asks: **Update all**, **Only missing** (new scripts,
yours left alone) or **Skip**. It always asks when the Scripts folder is
mounted from the host (`/config/.config/Skua/Scripts`), because "Update all"
replaces outdated scripts, local edits included. `SKUA_SCRIPT_SYNC=ask|off`
forces asking or skips the sync. The junk item list asks as in WPF when its
auto update is off. Without windows (`SKUA_UI=0`) the questions answer Skip /
No; `POST /scripts/update` on the control API syncs everything on demand.
Scripts that fail to download (a Scripts folder the container user cannot
write, say) are listed in a pop-up. The right end of the main window's status
line shows the sync as it runs (fetching the list, "Downloading scripts
120 / 451" with a progress bar, quest data, junk list, or waiting for your
answer), then its outcome for 15 s.

## Tabs, Army Control, Grid View

As WPF's `TabbedHostWindow`: by default this program is the tab host
(`TabHostWindow`), and each tab is another copy of it (`--tab-child`,
`SKUA_INSTANCE=N`) - a full Skua with its own game window, which main.js
opens for it (`/instances/N`, page `?instance=N`, its own browser storage).
The host re-parents each tab's main window under the tab strip; tabs not on
screen are parked out of view and slowed to 2 fps, Grid View tiles them all
with their menus hidden. Tab N's bridge and API listen on the configured
ports + 10*N; only the first tab syncs scripts and loads `SKUA_SCRIPT`.

Army Control sends every tab the same command through its control API
(`Skua.Linux/ArmyApi.cs`, `POST /army/...`): start/stop scripts, load a script
(the Script Repo's Load), the Army Scheduler (the Scheduler's playlist, run by
every tab), login/logout, jump to a map or player, accept a quest, and the
Misc Options toggles. `SKUA_TABS=0` runs a single Skua as before; `SKUA_TABS=N`
opens N tabs at start; `AQW_USER_N` / `AQW_PASS_N` log tab N in.

## Checking it

`--snapshot` renders the main window, the bot window, sample dialogs and
every registered view with Avalonia's headless renderer (Skia), with nothing
connected. `SNAPSHOT_START=1` also runs the app's startup sequence first
(runtime, theme, hotkeys).
