using System.Globalization;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Skua.Core.AppStartup;
using Skua.Core.Interfaces;
using Skua.Ruffle;

namespace Skua.Linux;

/// <summary>
/// Skua.Core on Linux, driving skua.swf in the vibeskua-web page through
/// Skua.Ruffle's bridge: the services, the bridge, the control API and the
/// script repository sync. Skua.Host runs it with no UI; Skua.App.Avalonia
/// runs it under its windows, replacing the headless stand-ins with real ones.
///
/// Environment:
///   SKUA_BRIDGE_PREFIX   where the page connects   (http://127.0.0.1:8790/)
///   SKUA_BRIDGE_ORIGINS  page origins allowed      (http://127.0.0.1:8770)
///   SKUA_API_PREFIX      control API               (http://127.0.0.1:8791/)
///   SKUA_SCRIPT          script to load at startup (the Script Loader shows
///                        it, ready to start); absolute, or a repository path
///                        such as Farm/GoldFarm
///   SKUA_SCRIPT_AUTO_START  1: also start it once logged in (default off)
///   SKUA_SCRIPTS_REPO    repository scripts sync from, GitHub or Gitea
///                        (https://github.com/auqw/Scripts)
///   SKUA_SCRIPTS_BRANCH  its branch (Skua)
/// </summary>
public sealed class SkuaRuntime
{
    private SkuaRuntime(RuffleBridge bridge, IServiceProvider services, ScriptSync scripts, HostApi api)
    {
        Bridge = bridge;
        Services = services;
        Scripts = scripts;
        Api = api;
    }

    public RuffleBridge Bridge { get; }
    public IServiceProvider Services { get; }
    public ScriptSync Scripts { get; }
    public HostApi Api { get; }

    /// <summary>
    /// An environment variable, tolerating surrounding quotes and spaces: in
    /// compose's list form, <c>- VAR="value"</c> passes the quotes through.
    /// </summary>
    public static string? EnvRaw(string name) =>
        Environment.GetEnvironmentVariable(name)?.Trim().Trim('"', '\'').Trim() is { Length: > 0 } v ? v : null;

    public static string Env(string name, string fallback) => EnvRaw(name) ?? fallback;

    /// <summary>
    /// Which tab (account) this Skua is, from SKUA_INSTANCE: 0 for the first
    /// or only one. The tab host (Skua.App.Avalonia/TabHost.cs) starts one
    /// Skua per tab and gives each its own ports.
    /// </summary>
    public static int Instance => int.TryParse(EnvRaw("SKUA_INSTANCE"), out int n) && n > 0 ? n : 0;

    // Each startup step, so a startup that hangs shows where.
    private static void Step(string what) => Console.Error.WriteLine($"[host] start: {what}");

    /// <summary>
    /// Builds the service provider. <paramref name="platformServices"/> runs
    /// after the headless stand-ins are registered, so whatever it registers
    /// replaces them (the container resolves the last registration).
    /// </summary>
    public static SkuaRuntime Create(Action<IServiceCollection>? platformServices = null)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

        var bridge = new RuffleBridge(
            Env("SKUA_BRIDGE_PREFIX", "http://127.0.0.1:8790/"),
            Env("SKUA_BRIDGE_ORIGINS", "http://127.0.0.1:8770").Split(',', StringSplitOptions.RemoveEmptyEntries));

        var services = new ServiceCollection();
        services.AddSingleton(bridge);
        services.AddSingleton<IFlashUtil>(s =>
            new RuffleFlashUtil(bridge, new Lazy<IScriptManager>(() => s.GetRequiredService<IScriptManager>())));
        services.AddHeadlessServices();
        platformServices?.Invoke(services);
        services.AddCommonServices();
        services.AddScriptableObjects();
        // Script options that can skip their window (ScriptOptionsWindow.cs);
        // after AddScriptableObjects, which registers Skua's own.
        services.AddTransient<IScriptOptionContainer, SkippableScriptOptionContainer>();
        services.AddCompiler();
        var provider = services.BuildServiceProvider();
        Ioc.Default.ConfigureServices(provider);

        var scripts = new ScriptSync(provider);
        var api = new HostApi(provider, scripts, Env("SKUA_API_PREFIX", "http://127.0.0.1:8791/"));
        return new SkuaRuntime(bridge, provider, scripts, api);
    }

    /// <summary>Starts everything; returns once the bridge and API are listening.</summary>
    public void Start(string? script = null)
    {
        var provider = Services;
        Step("settings");
        provider.GetRequiredService<ISettingsService>().SetApplicationVersion();
        Step("logs");
        _ = provider.GetRequiredService<ILogService>();
        Step("script interface");
        _ = provider.GetRequiredService<IScriptInterface>();   // hooks the SWF's events
        Step("client files");
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
        Step("plugins");
        provider.GetRequiredService<IPluginManager>().Initialize();
        Step("script sync, bridge, API");
        _ = Task.Run(Scripts.StartupAsync);

        Bridge.ConnectionChanged += up => Console.WriteLine(up ? "[host] page connected" : "[host] page disconnected");
        Bridge.Start();
        var keeper = new ScriptKeeper(provider, Bridge);
        keeper.Start();
        new OptionKeeper(provider, Bridge).Start();
        new MemoryTrim(provider).Start();
        AuraWatch.Start(provider, Bridge);
        if (NativeGame.Enabled)
        {
            // No Electron page: this tab runs the game itself and logs it in.
            Step("native game");
            new NativeGame(Env("SKUA_BRIDGE_PREFIX", "http://127.0.0.1:8790/")).Start();
            new NativeSession(provider, Bridge, keeper).Start();
        }
        string apiPrefix = Env("SKUA_API_PREFIX", "http://127.0.0.1:8791/");
        try
        {
            Api.Start();
        }
        catch (Exception e)
        {
            // Keep the bot (and SKUA_SCRIPT) running without its API rather
            // than restarting forever over a bad setting.
            Console.Error.WriteLine($"[host] control API not started ({apiPrefix}): {e.Message}");
        }
        Console.WriteLine($"[host] ready: bridge {Env("SKUA_BRIDGE_PREFIX", "http://127.0.0.1:8790/")}, api {apiPrefix}");

        if (RoomNumber.Value is not null)
        {
            // Each account that logs in gets SKUA_ROOM_NUMBER in its CoreBots options.
            _ = Task.Run(async () =>
            {
                var player = provider.GetRequiredService<IScriptInterface>().Player;
                string? last = null;
                while (true)
                {
                    try
                    {
                        if (Bridge.IsConnected && player.LoggedIn && player.Username is { Length: > 0 } user && user != last)
                        {
                            RoomNumber.Apply(user);
                            last = user;
                        }
                    }
                    catch { }
                    await Task.Delay(3000);
                }
            });
        }

        if ((script ?? EnvRaw("SKUA_SCRIPT")) is { Length: > 0 } toLoad)
        {
            bool autoStart = script is not null || EnvRaw("SKUA_SCRIPT_AUTO_START") is { } a
                && a.ToLowerInvariant() is "1" or "true" or "yes" or "on";
            _ = Task.Run(async () =>
            {
                // Loading may wait for the script sync to fetch it.
                if (await Api.LoadScriptFile(toLoad) is { } loadError)
                {
                    Console.Error.WriteLine($"[host] could not load {toLoad}: {loadError}");
                    return;
                }
                Console.WriteLine($"[host] loaded {toLoad}" + (autoStart ? "; starting it once logged in" : " (SKUA_SCRIPT_AUTO_START is off: start it from Skua)"));
                if (!autoStart)
                    return;
                var bot = provider.GetRequiredService<IScriptInterface>();
                while (!(Bridge.IsConnected && bot.Player.LoggedIn))
                    await Task.Delay(2000);
                // Not while a sync (this tab's, or the first tab's for the
                // others) is rewriting the scripts it compiles from.
                if (!await ScriptSync.WaitUntilQuietAsync(TimeSpan.FromMinutes(15),
                        () => Console.WriteLine($"[host] logged in; waiting for the script sync to finish before starting {toLoad}")))
                    Console.Error.WriteLine("[host] the script sync is still running after 15 minutes; starting anyway");
                RoomNumber.Apply(bot.Player.Username);   // before CoreBots reads its options
                Console.WriteLine($"[host] logged in; starting {toLoad}");
                if (await Api.StartLoadedAsync() is { } error)
                    Console.Error.WriteLine($"[host] script failed to start: {error}");
            });
        }
    }
}
