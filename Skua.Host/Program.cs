// Skua.Host: Skua.Core without a UI, driving skua.swf in the vibeskua-web
// page through Skua.Ruffle's WebSocket bridge.
//
// Environment:
//   SKUA_BRIDGE_PREFIX   where the page connects   (http://127.0.0.1:8790/)
//   SKUA_BRIDGE_ORIGINS  page origins allowed      (http://127.0.0.1:8770)
//   SKUA_API_PREFIX      control API               (http://127.0.0.1:8791/)
//   SKUA_SCRIPT          script to start once logged in (or --script <path>)
//
// Control API (JSON; local only):
//   GET  /status                      bridge, login, map, script state
//   POST /script/start?path=<file>    load and start a script (.cs)
//   POST /script/start   (body: C#)   start a script given as source
//   POST /script/stop
//   GET  /log?type=script|debug|flash&since=<n>

using System.Globalization;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Skua.Core.AppStartup;
using Skua.Core.Interfaces;
using Skua.Host;
using Skua.Ruffle;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;
string? Arg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

var bridge = new RuffleBridge(
    Env("SKUA_BRIDGE_PREFIX", "http://127.0.0.1:8790/"),
    Env("SKUA_BRIDGE_ORIGINS", "http://127.0.0.1:8770").Split(',', StringSplitOptions.RemoveEmptyEntries));

var services = new ServiceCollection();
services.AddSingleton(bridge);
services.AddSingleton<IFlashUtil>(s =>
    new RuffleFlashUtil(bridge, new Lazy<IScriptManager>(() => s.GetRequiredService<IScriptManager>())));
services.AddHeadlessServices();
services.AddCommonServices();
services.AddScriptableObjects();
services.AddCompiler();
var provider = services.BuildServiceProvider();
Ioc.Default.ConfigureServices(provider);

provider.GetRequiredService<ISettingsService>().SetApplicationVersion();
var log = provider.GetRequiredService<ILogService>();
_ = provider.GetRequiredService<IScriptInterface>();   // hooks the SWF's events
try
{
    provider.GetRequiredService<IClientFilesService>().CreateDirectories();
    provider.GetRequiredService<IClientFilesService>().CreateFiles();
}
catch (Exception e)
{
    Console.Error.WriteLine($"[host] client files: {e.Message}");
}
_ = Task.Run(async () =>
{
    try { await provider.GetRequiredService<IScriptServers>().GetServers(); }
    catch (Exception e) { Console.Error.WriteLine($"[host] server list: {e.Message}"); }
});
provider.GetRequiredService<IPluginManager>().Initialize();

bridge.ConnectionChanged += up => Console.WriteLine(up ? "[host] page connected" : "[host] page disconnected");
bridge.Start();
var api = new HostApi(provider, Env("SKUA_API_PREFIX", "http://127.0.0.1:8791/"));
api.Start();
Console.WriteLine("[host] ready: bridge " + Env("SKUA_BRIDGE_PREFIX", "http://127.0.0.1:8790/") + ", api " + Env("SKUA_API_PREFIX", "http://127.0.0.1:8791/"));

if ((Arg("--script") ?? Environment.GetEnvironmentVariable("SKUA_SCRIPT")) is { Length: > 0 } script)
{
    _ = Task.Run(async () =>
    {
        var bot = provider.GetRequiredService<IScriptInterface>();
        while (!(bridge.IsConnected && bot.Player.LoggedIn))
            await Task.Delay(2000);
        Console.WriteLine($"[host] logged in; starting {script}");
        var error = await api.StartScriptFile(script);
        if (error is not null)
            Console.Error.WriteLine($"[host] script failed to start: {error}");
    });
}

await Task.Delay(Timeout.Infinite);
