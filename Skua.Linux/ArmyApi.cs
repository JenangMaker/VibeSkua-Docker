using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Skua.Core.Interfaces;
using Skua.Core.ViewModels;

namespace Skua.Linux;

/// <summary>
/// POST /army/...: what the tab host's Army Control sends every tab
/// (Skua.App.Avalonia/TabHost.cs), as Skua.App.WPF's TabbedHostWindow did
/// with window messages to EmbeddedMainWindow. Each does the same as there.
///
///   login                      relogin (Options > Relogin server, else Twilly)
///   logout
///   jump?map=&amp;cell=            join map (or jump in this one); cell may be "Cell,Pad"
///   goto?player=               go to a player
///   quest?id=&amp;item=            register a quest to accept (item: reward id)
///   option?name=&amp;value=        LagKiller, HeadlessMode, HidePlayers, DisableFX,
///                              InfiniteRange, Magnetise, SkipCutscenes,
///                              UseFunctionBasedSkills, StreamerMode
///   GET options                those options' current values, as {name: bool}
///   start                      start the loaded script
///   stop                       stop the scheduler if it runs, else the script
///   scheduler                  body: [{path,id,name}]; replace the Scheduler's
///                              playlist with it and start it
///   scheduler/stop
///   throttle?on=1|0[&amp;fps=]      the tab is off screen: low frame rate (fps, else
///                              SKUA_HIDDEN_FPS, else 2) and a 1x1 game window;
///                              Headless Mode does the same at 1 fps (TabThrottle.cs)
/// </summary>
public sealed partial class HostApi
{
    private static readonly string[] ArmyOptions =
    [
        "LagKiller", "HeadlessMode", "HidePlayers", "DisableFX", "InfiniteRange",
        "Magnetise", "SkipCutscenes", "UseFunctionBasedSkills", "StreamerMode",
    ];

    private T Get<T>() where T : notnull => services.GetRequiredService<T>();

    private Dictionary<string, bool> ArmyOptionValues()
    {
        var options = Get<IScriptOption>();
        return ArmyOptions.ToDictionary(name => name, name => (bool)typeof(IScriptOption).GetProperty(name)!.GetValue(options)!);
    }

    private async Task<object> Army(string action, HttpListenerRequest request)
    {
        string? Q(string name) => request.QueryString[name]?.Trim() is { Length: > 0 } v ? v : null;
        var bot = Get<IScriptInterface>();
        if (action is "login" or "logout" or "jump" or "goto" or "quest" or "start"
            && !Get<Skua.Ruffle.RuffleBridge>().IsConnected)
            return new { error = "the game page is not connected to this Skua yet" };
        switch (action)
        {
            case "login":
            {
                var options = Get<IScriptOption>();
                string server = string.IsNullOrWhiteSpace(options.ReloginServer) ? "Twilly" : options.ReloginServer!;
                bool ok = await Task.Run(() => Get<IScriptServers>().Relogin(server));
                return new { login = ok, server };
            }
            case "logout":
                Get<IScriptServers>().Logout();
                return new { logout = true };
            case "jump":
            {
                string map = Q("map") ?? "";
                string cell = Q("cell") ?? "Enter", pad = "Spawn";
                if (cell.Contains(','))
                {
                    var parts = cell.Split(',');
                    cell = parts[0].Trim();
                    pad = parts.Length > 1 && parts[1].Trim().Length > 0 ? parts[1].Trim() : pad;
                }
                await Task.Run(() =>
                {
                    if (map.Length == 0 || bot.Map.Name.Equals(map.Split('-')[0], StringComparison.OrdinalIgnoreCase))
                        bot.Map.Jump(cell, pad, false);
                    else
                        bot.Map.Join(map, cell, pad, true, false);
                });
                return new { map, cell, pad };
            }
            case "goto":
            {
                if (Q("player") is not { } player)
                    return new { error = "give ?player=<name>" };
                await Task.Run(() => bot.Player.Goto(player));
                return new { player };
            }
            case "quest":
            {
                if (!int.TryParse(Q("id"), out int id) || id <= 0)
                    return new { error = "give ?id=<quest id>" };
                int reward = int.TryParse(Q("item"), out int r) ? r : -1;
                bot.Quests.RegisterQuests((id, reward));
                return new { quest = id, reward };
            }
            case "option":
            {
                string? name = ArmyOptions.FirstOrDefault(o => o.Equals(Q("name"), StringComparison.OrdinalIgnoreCase));
                if (name is null)
                    return new { error = $"name must be one of {string.Join(", ", ArmyOptions)}" };
                bool value = Q("value")?.ToLowerInvariant() is "1" or "true" or "yes" or "on";
                var options = Get<IScriptOption>();
                Get<IDispatcherService>().Invoke(() =>
                {
                    options.IsIpcMessageProcessing = true;
                    try { typeof(IScriptOption).GetProperty(name)!.SetValue(options, value); }
                    finally { options.IsIpcMessageProcessing = false; }
                });
                return new { option = name, value };
            }
            case "start":
            {
                string ident = bot.Player.LoggedIn ? bot.Player.Username : "Offline Account";
                string? error = !bot.Player.LoggedIn ? "account is not logged in" : await StartLoadedAsync();
                if (error is not null)
                    Get<ILogService>().ScriptLog($"[{ident}] Army start ignored: {error}");
                return error is null ? new { started = Get<IScriptManager>().LoadedScript } : new { error };
            }
            case "stop":
            {
                var scheduler = services.GetService<ScriptSchedulerViewModel>();
                if (scheduler is { IsRunningQueue: true })
                    await OnUi(() => scheduler.StopQueueCommand.ExecuteAsync(null));
                else
                    await Get<IScriptManager>().StopScript();
                return new { stopped = true };
            }
            case "scheduler":
            {
                if (services.GetService<ScriptSchedulerViewModel>() is not { } scheduler)
                    return new { error = "no Scheduler here (running without windows)" };
                using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
                var items = JsonSerializer.Deserialize<List<ScriptSchedulerViewModel.SavedScriptItem>>(
                    await reader.ReadToEndAsync(), InputJson) ?? [];
                int queued = 0;
                await OnUi(() =>
                {
                    scheduler.ScriptQueue.Clear();
                    foreach (var item in items.Where(i => File.Exists(i.Path)))
                    {
                        var vm = new ScriptItemViewModel(item.Path) { Id = item.Id };
                        if (!string.IsNullOrEmpty(item.Name))
                            vm.Name = item.Name;
                        scheduler.ScriptQueue.Add(vm);
                    }
                    queued = scheduler.ScriptQueue.Count;
                    if (queued > 0 && !scheduler.IsRunningQueue && scheduler.StartQueueCommand.CanExecute(null))
                        scheduler.StartQueueCommand.Execute(null);
                    return Task.CompletedTask;
                });
                return new { queued, skipped = items.Count - queued };
            }
            case "scheduler/stop":
            {
                if (services.GetService<ScriptSchedulerViewModel>() is { } scheduler)
                    await OnUi(() => scheduler.StopQueueCommand.ExecuteAsync(null));
                return new { stopped = true };
            }
            case "throttle":
                return Throttle(Q("on") is "1" or "true",
                    int.TryParse(Q("fps"), out int fps) && fps > 0 ? Math.Min(fps, 60) : HiddenFps);
            default:
                return new { error = $"unknown army action '{action}'" };
        }
    }

    // Runs on the UI thread (the view models' collections are bound there) and
    // waits for it, including any task it returns.
    private async Task OnUi(Func<Task> action)
    {
        Task? inner = null;
        Get<IDispatcherService>().Invoke(() => inner = action());
        if (inner is not null)
            await inner;
    }
}
