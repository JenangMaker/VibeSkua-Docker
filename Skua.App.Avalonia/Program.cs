// VibeSkua for Linux: Skua's UI (ported from Skua.App.WPF to Avalonia) over
// Skua.Linux's runtime. The game runs in the vibeskua-web page; this process
// is the bot and its windows. Environment: see Skua.Linux/SkuaRuntime.cs.
//
//   Skua.App.Avalonia                  windows + bot + control API
//   Skua.App.Avalonia --headless       bot + control API only (as Skua.Host)
//   Skua.App.Avalonia --snapshot DIR   render every view to DIR/*.png and exit
//   Skua.App.Avalonia --script-options SCRIPT OUT [ACCOUNT]   a script's options as JSON, then exit

using Avalonia;
using Skua.App.Avalonia;
using Skua.App.Avalonia.Services;
using Skua.Core.AppStartup;
using Skua.Linux;

if (args.Contains("--headless"))
{
    var headless = SkuaRuntime.Create();
    headless.Start();
    await Task.Delay(Timeout.Infinite);
    return;
}

if (Array.IndexOf(args, "--fake-game") is int f and >= 0)
{
    FakeGame.Run(f + 1 < args.Length && int.TryParse(args[f + 1], out int port) ? port : 8770);
    return;
}

// A script's options, read in a process of its own (Skua.Linux/ScriptOptionsProbe.cs).
if (Array.IndexOf(args, "--script-options") is int o and >= 0 && o + 2 < args.Length)
{
    Environment.Exit(ScriptOptionsProbe.Run(args[o + 1], args[o + 2], o + 3 < args.Length ? args[o + 3] : null));
}

if (Array.IndexOf(args, "--snapshot") is int s and >= 0 && s + 1 < args.Length)
{
    Snapshot.Run(args[s + 1], s + 2 < args.Length ? args[s + 2] : null);
    return;
}

// Avalonia 11 on Linux draws through X11 only.
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
{
    Console.Error.WriteLine("[host] no display (DISPLAY unset); running without windows");
    var headless = SkuaRuntime.Create();
    headless.Start();
    await Task.Delay(Timeout.Infinite);
    return;
}

XConnectionLoss.Install();

// Tabs, as Skua.App.WPF's TabbedHostWindow: this process draws the tab strip
// and starts one Skua per tab (--tab-child), each with its own game. On by
// default; SKUA_TABS=0 runs a single Skua, SKUA_TABS=N opens N tabs.
App.Mode = args.Contains("--tab-child") ? AppMode.TabChild
    : TabHostWindow.Enabled ? AppMode.TabHost
    : AppMode.Single;
if (App.Mode == AppMode.TabChild)
    TabHostWindow.WatchHost();

App.Runtime = SkuaRuntime.Create(services =>
{
    services.AddAvaloniaServices();
    services.AddSkuaMainAppViewModels();
});
// The tab host only uses Skua's services for its dialogs (Scheduler, Script
// Repo); the bot, bridge and API run in the tabs.
App.StartRuntime = App.Mode != AppMode.TabHost;
// Avalonia's warnings and errors into the log as [ui] lines (UiDiagnostics.cs).
Avalonia.Logging.Logger.Sink = new UiLogSink();
// SKUA_UI_GPU=0 draws Skua's windows in software instead of OpenGL (GLX), Avalonia's
// default when a GPU is reachable. The games are not affected. Under VNC the pixels go to
// memory anyway, and each window held a GL context next to the games' Vulkan ones.
bool uiGpu = SkuaRuntime.EnvRaw("SKUA_UI_GPU")?.Trim().ToLowerInvariant() is not ("0" or "false" or "no");
AppBuilder.Configure<App>()
    .UsePlatformDetect()
    .With(new X11PlatformOptions { RenderingMode = uiGpu ? new[] { X11RenderingMode.Glx, X11RenderingMode.Software } : new[] { X11RenderingMode.Software } })
    .WithInterFont()
    .StartWithClassicDesktopLifetime(args);
