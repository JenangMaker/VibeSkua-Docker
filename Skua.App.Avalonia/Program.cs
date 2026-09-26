// VibeSkua for Linux: Skua's UI (ported from Skua.App.WPF to Avalonia) over
// Skua.Linux's runtime. The game runs in the vibeskua-web page; this process
// is the bot and its windows. Environment: see Skua.Linux/SkuaRuntime.cs.
//
//   Skua.App.Avalonia                  windows + bot + control API
//   Skua.App.Avalonia --headless       bot + control API only (as Skua.Host)
//   Skua.App.Avalonia --snapshot DIR   render every view to DIR/*.png and exit

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

App.Runtime = SkuaRuntime.Create(services =>
{
    services.AddAvaloniaServices();
    services.AddSkuaMainAppViewModels();
});
AppBuilder.Configure<App>()
    .UsePlatformDetect()
    .WithInterFont()
    .LogToTrace()
    .StartWithClassicDesktopLifetime(args);
