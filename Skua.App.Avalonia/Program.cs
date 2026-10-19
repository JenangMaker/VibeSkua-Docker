// VibeSkua for Linux: Skua's UI (ported from Skua.App.WPF to Avalonia) over
// Skua.Linux's runtime. The game runs in the vibeskua-web page; this process
// is the bot and its windows. Environment: see Skua.Linux/SkuaRuntime.cs.
//
//   Skua.App.Avalonia                  windows + bot + control API
//   Skua.App.Avalonia --headless       bot + control API only (as Skua.Host)
//   Skua.App.Avalonia --snapshot DIR   render every view to DIR/*.png and exit
//   Skua.App.Avalonia --script-options SCRIPT OUT [ACCOUNT]   a script's options as JSON, then exit

using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Skua.App.Avalonia;
using Skua.App.Avalonia.Services;
using Skua.Core.AppStartup;
using Skua.Linux;

// Linux: .NET gives an empty ApplicationData path when ~/.config (or
// $XDG_CONFIG_HOME) does not exist yet, as in a fresh account, and Skua's
// files then went into whatever folder the app was started from. Before
// anything reads that path.
if (OperatingSystem.IsLinux()
    && Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify) is { Length: > 0 } config)
{
    try { Directory.CreateDirectory(config); } catch { }
}

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
if (OperatingSystem.IsLinux() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
{
    Console.Error.WriteLine("[host] no display (DISPLAY unset); running without windows");
    var headless = SkuaRuntime.Create();
    headless.Start();
    await Task.Delay(Timeout.Infinite);
    return;
}

if (OperatingSystem.IsLinux())
    XConnectionLoss.Install();

// Tabs, as Skua.App.WPF's TabbedHostWindow: this process draws the tab strip
// and starts one Skua per tab (--tab-child), each with its own game. On by
// default; SKUA_TABS=0 runs a single Skua, SKUA_TABS=N opens N tabs.
// VibeSkua Manager: "--manager", or an executable named like "VibeSkua Manager.exe"
// (a copy of this one, as the Windows release ships it).
bool manager = args.Contains("--manager")
    || Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "").Contains("Manager", StringComparison.OrdinalIgnoreCase);
if (manager)
    HeadlessSettingsService.Role = Skua.Core.Models.AppRole.Manager;
App.Mode = manager ? AppMode.Manager
    : args.Contains("--tab-child") ? AppMode.TabChild
    : TabHostWindow.Enabled ? AppMode.TabHost
    : AppMode.Single;
if (App.Mode == AppMode.TabChild)
    TabHostWindow.WatchHost();
// The desktop client: a log file (see LogFile); a tab's lines reach it through the tab host.
else if (SkuaRuntime.DesktopClient)
    LogFile.Install(App.Mode == AppMode.Manager ? "manager" : "vibeskua");

App.Runtime = SkuaRuntime.Create(services =>
{
    services.AddAvaloniaServices();
    services.AddSkuaMainAppViewModels();
    if (manager)
    {
        services.AddSkuaManagerViewModels();
        services.AddSingleton<Skua.App.Avalonia.Manager.RunningTabsViewModel>();
        services.AddSingleton<Skua.Core.Interfaces.IClientLauncher, Skua.App.Avalonia.Manager.HostLauncher>();
        // No client updater: Skua's downloads auqw/Skua releases, which would
        // replace VibeSkua with the original client. New VibeSkua builds come
        // from its own releases page.
        services.RemoveAll<Skua.Core.ViewModels.Manager.AppUpdaterViewModel>();
        services.RemoveAll<Skua.Core.ViewModels.Manager.ClientUpdatesViewModel>();
        services.RemoveAll<Skua.Core.Interfaces.IClientUpdateService>();
    }
});
// The tab host only uses Skua's services for its dialogs (Scheduler, Script
// Repo); the bot, bridge and API run in the tabs. The Manager runs no bot either.
App.StartRuntime = App.Mode is not (AppMode.TabHost or AppMode.Manager);
// Windows: windows with a redirection surface, not composition (WinUI or
// DirectComposition) ones. Those are created with WS_EX_NOREDIRECTIONBITMAP,
// and the game is a child window of ours: AMD's Vulkan driver (seen on a
// Ryzen 5500U's iGPU) presents a child window into its top-level window's
// redirection surface, so with none the game was never shown and its area was
// see-through. DX12 (flip model) was shown either way. Ignored elsewhere.
AppBuilder.Configure<App>()
    .UsePlatformDetect()
    .With(new Win32PlatformOptions { CompositionMode = [Win32CompositionMode.RedirectionSurface] })
    .WithInterFont()
    .LogToTrace()
    .StartWithClassicDesktopLifetime(args);
