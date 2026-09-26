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
| `HostWindow` / `HostDialog` / `BotWindow` / `MainWindow` | same names; `MainWindow` is the menu over a status line, since the game is in the page |
| Behaviours | `Behaviors.cs` attached properties (`b:Behave.OnlyNumbers`, `CopySelected`, `ScrollToEnd`, `DeleteSelected`, ...) |
| `ICollectionView` filters | `UiMirror<T>`: a filtered UI-thread copy (the script list changes on other threads) |
| `ListBox.SelectedItems` as a command parameter | `Selection.Run(command, list)` in code-behind (Avalonia's is not an `IList<object>`) |
| `PropertyGrid` (grabber) | `PropertyInspector`: read-only property list |
| MaterialDesign theme + palette editor | Fluent in Skua's colours; `AvaloniaThemeService` reads/writes the same `CurrentTheme`/`UserThemes` settings (base theme and colours; MaterialDesign's contrast adjustment has no counterpart) |
| MdXaml | Markdown.Avalonia.Tight |

Every view of the client is ported. Not ported: the Flash host windows
(`TabbedHostWindow`, `EmbeddedMainWindow`, `GameContainerUserControl`: the
game is the page), the tray icon and balloon tips, and the Skua.Manager
views (account manager, launcher, updaters, groups), which belong to a
separate app.

Hotkeys work while a Skua window has focus, as in WPF (they are key
bindings on the main window, not global).

## Checking it

`--snapshot` renders the main window, the bot window, sample dialogs and
every registered view with Avalonia's headless renderer (Skia), with nothing
connected. `SNAPSHOT_START=1` also runs the app's startup sequence first
(runtime, theme, hotkeys).
