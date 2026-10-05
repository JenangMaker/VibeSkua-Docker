using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Messaging;
using Skua.Ruffle;

namespace Skua.Linux;

/// <summary>
/// The control API: start/stop scripts, read status and logs. Binds to a
/// local address only; anything that can reach it can run code as the bot.
/// </summary>
public sealed partial class HostApi(IServiceProvider services, ScriptSync scripts, string prefix)
{
    // One instance each, reused: System.Text.Json caches the serialization code
    // it generates per options instance, so a new one per reply generated it
    // again on every request, and the manager and the tab host poll /status
    // all the time. The dynamic methods left behind kept the finalizer thread
    // busy: about a core per idle Skua.
    private static readonly JsonSerializerOptions ReplyJson = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions InputJson = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpListener _listener = new();
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "skua-host");

    /// <summary>
    /// Routes the UI adds (e.g. GET /ui/window), by "METHOD /path". Checked
    /// after the built-in ones.
    /// </summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, Func<HttpListenerRequest, Task<object>>> Routes { get; } = new();

    public void Start()
    {
        _listener.Prefixes.Add(prefix);
        _listener.Start();
        _ = Task.Run(Loop);   // off any caller's synchronization context
        WatchHeadless();
    }

    private async Task Loop()
    {
        while (_listener.IsListening)
        {
            var ctx = await _listener.GetContextAsync();
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private async Task Handle(HttpListenerContext ctx)
    {
        object result;
        int status = 200;
        try
        {
            string path = ctx.Request.Url!.AbsolutePath.TrimEnd('/');
            string method = ctx.Request.HttpMethod;
            result = !ApiAuth.Allowed(ctx.Request) ? Unauthorized(out status) : (method, path) switch
            {
                ("GET", "/status") => Status(ctx.Request.QueryString["detail"] is "1" or "true"),
                ("POST", "/script/load") => await LoadFromRequest(ctx.Request),
                ("POST", "/script/start") => await StartFromRequest(ctx.Request),
                ("POST", "/script/stop") => await Stop(),
                ("GET", "/script/options") => await ScriptOptions(),
                ("POST", "/script/options") => await SaveScriptOptions(ctx.Request),
                ("GET", "/log") => Log(ctx.Request),
                ("GET", "/scripts") => await Scripts(ctx.Request),
                ("GET", "/scripts/categories") => ScriptSync.Categories,
                ("GET", "/scripts/browse") => scripts.Browse(ctx.Request.QueryString["dir"]),
                ("POST", "/scripts/update") => await scripts.UpdateAllAsync(),
                ("POST", "/scripts/reset") => await scripts.ResetScriptsAsync(),
                ("GET", "/army/options") => ArmyOptionValues(),
                ("POST", "/debug/trace") => await TraceApi.Collect(ctx.Request),
                ("GET", "/render") => Render(null, null),
                ("POST", "/render") => Render(ctx.Request.QueryString["fps"], ctx.Request.QueryString["game"]),
                ("POST", _) when path.StartsWith("/army/") => await Army(path["/army/".Length..], ctx.Request),
                _ when Routes.TryGetValue($"{method} {path}", out var route) => await route(ctx.Request),
                _ => NotFound(out status),
            };
        }
        catch (Exception e)
        {
            status = 500;
            result = new { error = e.Message };
        }

        byte[] body = JsonSerializer.SerializeToUtf8Bytes(result, ReplyJson);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.OutputStream.WriteAsync(body);
        ctx.Response.Close();
    }

    private static object NotFound(out int status)
    {
        status = 404;
        return new { error = "not found" };
    }

    private static object Unauthorized(out int status)
    {
        status = 401;
        return new { error = "SKUA_API_TOKEN is set: send it as Authorization: Bearer <token>" };
    }

    // The game's drawing rate: fps=1-60 caps the pictures drawn a second,
    // fps=none lifts the cap (the game's own frame rate then, 24 or 30). Hidden
    // and Headless tabs draw nothing either way. game=1-60 sets the game's own
    // frame rate through Skua's FPS option (Options > SetFPS, default 30; AQW
    // itself runs at 24): the game's logic, not the drawing, is most of what a
    // tab that draws costs. Answers with the page's render state (fps, scale,
    // paused) and the game's frame rate.
    private object Render(string? fps, string? game)
    {
        var bridge = services.GetRequiredService<RuffleBridge>();
        if (!bridge.IsConnected)
            return new { error = "the game is not connected" };
        if (fps is not null)
        {
            object cap = int.TryParse(fps, out int n) && n > 0 ? Math.Min(n, 60) : "none";
            bridge.Invoke("page.setRender", new Dictionary<string, object?> { ["fps"] = cap });
        }
        if (int.TryParse(game, out int rate) && rate is >= 1 and <= 60)
        {
            var options = services.GetRequiredService<IScriptOption>();
            services.GetRequiredService<IDispatcherService>().Invoke(() => options.SetFPS = rate);
        }
        var bot = services.GetRequiredService<IScriptInterface>();
        return new
        {
            render = bridge.Invoke("page.getRender"),
            gameFps = bot.Flash.GetGameObject("stage.frameRate"),
            fpsOption = bot.Options.SetFPS,
        };
    }

    // detail: also what a remote dashboard shows (the web manager). Each field
    // is a call into the game, so the tab host's 1.5 s poll asks without it.
    private object Status(bool detail = false)
    {
        var bridge = services.GetRequiredService<RuffleBridge>();
        var manager = services.GetRequiredService<IScriptManager>();
        object? game = null, stats = null, combat = null, quests = null, equipment = null;
        if (bridge.IsConnected)
        {
            var bot = services.GetRequiredService<IScriptInterface>();
            var player = bot.Player;
            bool loggedIn = player.LoggedIn;
            // The room number, from the area name ("battleon-9721"), which
            // Streamer Mode does not rewrite.
            string? room = null;
            if (loggedIn && bot.Map.FullName is { } area && area.LastIndexOf('-') is > 0 and var dash
                && int.TryParse(area[(dash + 1)..], out _))
                room = area[(dash + 1)..];
            game = !detail || !loggedIn
                ? new
                {
                    loggedIn,
                    player = player.Username,
                    streamer = bot.Options.StreamerMode,
                    map = bot.Map.Name,
                    room,
                    cell = player.Cell,
                    hp = player.Health,
                }
                : new
                {
                    loggedIn,
                    player = player.Username,
                    streamer = bot.Options.StreamerMode,
                    map = bot.Map.Name,
                    room,
                    cell = player.Cell,
                    hp = player.Health,
                    maxHp = player.MaxHealth,
                    mp = player.Mana,
                    maxMp = player.MaxMana,
                    level = player.Level,
                    gold = player.Gold,
                    className = player.CurrentClass?.Name,
                    state = player.State,
                    hasTarget = player.HasTarget,
                    afk = player.AFK,
                };
            if (detail)
            {
                var s = bot.Stats;
                stats = new { kills = s.Kills, drops = s.Drops, questsCompleted = s.QuestsCompleted, deaths = s.Deaths, relogins = s.Relogins };
                if (loggedIn)
                {
                    combat = Combat(bot);
                    quests = Quests(bot);
                    equipment = Equipment(bot);
                }
            }
        }
        return new
        {
            instance = SkuaRuntime.Instance,
            bridgeConnected = bridge.IsConnected,
            game,
            stats,
            combat,
            quests,
            equipment,
            throttle = detail ? new { hidden = IsShrunk, headless = IsHeadless } : null,
            script = new { running = manager.ScriptRunning, loaded = manager.LoadedScript },
            scripts = new
            {
                directory = ClientFileSources.SkuaScriptsDIR,
                syncing = scripts.Syncing,
                last = scripts.LastResult,
            },
        };
    }

    /// <summary>
    /// The fight: the player's target and the monsters in its cell (at most 20),
    /// each with HP and state (0 dead, 1 idle, 2 in combat). Null if the game
    /// could not say.
    /// </summary>
    private static object? Combat(IScriptInterface bot)
    {
        static object Monster(Skua.Core.Models.Monsters.Monster m) =>
            new { id = m.MapID, name = m.Name, hp = m.HP, maxHp = m.MaxHP, state = m.State };
        try
        {
            return new
            {
                // No target reads as an empty monster (getTargetMonster's default).
                target = bot.Player.Target is { Name.Length: > 0 } t ? Monster(t) : null,
                monsters = bot.Monsters.CurrentMonsters.Take(20).Select(Monster).ToList(),
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// What the character wears: each equipped item's slot (class, weapon,
    /// armor, helm, cape, pet, necklace, ground, in that order), name and
    /// enhancement, and the special (proc) one if any. One read of the
    /// inventory. Null if the game could not say.
    /// </summary>
    private static object? Equipment(IScriptInterface bot)
    {
        // The item's sES: the slot it is worn in.
        static (int Order, string Slot) SlotOf(string? group) => group?.ToLowerInvariant() switch
        {
            "ar" => (0, "Class"),
            "weapon" => (1, "Weapon"),
            "co" => (2, "Armor"),
            "he" => (3, "Helm"),
            "ba" => (4, "Cape"),
            "pe" => (5, "Pet"),
            "am" => (6, "Necklace"),
            "mi" => (7, "Ground"),
            _ => (8, group ?? "Other"),
        };
        try
        {
            return bot.Inventory.Items
                .Where(i => i.Equipped)
                .Select(i => (Slot: SlotOf(i.ItemGroup), Item: i))
                .OrderBy(x => x.Slot.Order)
                .Select(x => new
                {
                    slot = x.Slot.Slot,
                    name = x.Item.Name,
                    enhancement = EnhancementName(x.Item.EnhancementPatternID),
                    proc = ProcName(x.Item.ProcID),
                })
                .ToList();
        }
        catch
        {
            return null;
        }
    }

    // As LoadoutService names them (ItemBase's EnhancementPatternID and ProcID).
    private static string? EnhancementName(int id) => id switch
    {
        1 => "Adventurer", 2 => "Fighter", 3 => "Thief", 4 => "Armsman", 5 => "Hybrid",
        6 => "Wizard", 7 => "Healer", 8 => "Spellbreaker", 9 => "Lucky", 10 => "Forge",
        11 => "Absolution", 12 => "Avarice", 23 => "Depths", 24 => "Vainglory", 25 => "Vim",
        26 => "Examen", 27 => "Pneuma", 28 => "Anima", 29 => "Penitence", 30 => "Lament",
        32 => "Hearty",
        _ => null,
    };

    private static string? ProcName(int id) => id switch
    {
        2 => "Spiral Carve", 3 => "Awe Blast", 4 => "Health Vamp", 5 => "Mana Vamp",
        6 => "Powerword DIE", 7 => "Lacerate", 8 => "Smite", 9 => "Valiance",
        10 => "Arcana's Concerto", 11 => "Acheron", 12 => "Elysium", 13 => "Praxis",
        14 => "Dauntless", 15 => "Ravenous",
        _ => null,
    };

    /// <summary>
    /// The quests in progress (at most 10), each requirement with how many
    /// the player has, in Flash's order; registered: the script completes it
    /// by itself (Quests.RegisterQuests). One read of the quest tree (~40 KB,
    /// a few ms) and of the inventories. Null if the game could not say.
    /// </summary>
    private static object? Quests(IScriptInterface bot)
    {
        try
        {
            var active = bot.Quests.Active;
            if (active.Count == 0)
                return new List<object>();
            // Held counts by item id, inventory and temporary items together.
            var held = new Dictionary<int, int>();
            foreach (var item in bot.Inventory.Items.Cast<Skua.Core.Models.Items.ItemBase>().Concat(bot.TempInv.Items))
                held[item.ID] = held.GetValueOrDefault(item.ID) + item.Quantity;
            var registered = bot.Quests.Registered.ToHashSet();
            return active.Take(10).Select(q =>
            {
                var reqs = q.Requirements.Select(r => new
                {
                    id = r.ID,
                    name = r.Name,
                    have = Math.Min(held.GetValueOrDefault(r.ID), Math.Max(r.Quantity, 0)),
                    need = r.Quantity,
                    temp = r.Temp,
                }).ToList();
                return new
                {
                    id = q.ID,
                    name = q.Name,
                    registered = registered.Contains(q.ID),
                    ready = reqs.All(r => r.have >= r.need),
                    requirements = reqs,
                };
            }).ToList();
        }
        catch
        {
            return null;
        }
    }

    // ?path=<file>, or the script source as the body; null when neither is given.
    private async Task<string?> ScriptFromRequest(HttpListenerRequest request)
    {
        if (request.QueryString["path"] is { } file)
            return file;
        using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
        string source = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(source))
            return null;
        Directory.CreateDirectory(_scratch);
        file = Path.Combine(_scratch, $"api-{DateTime.UtcNow:yyyyMMdd-HHmmss}.cs");
        await File.WriteAllTextAsync(file, source);
        return file;
    }

    private async Task<object> LoadFromRequest(HttpListenerRequest request)
    {
        if (await ScriptFromRequest(request) is not { } file)
            return new { error = "give ?path=<file> or the script source as the body" };
        var error = await LoadScriptFile(file);
        return error is null ? new { loaded = services.GetRequiredService<IScriptManager>().LoadedScript } : new { error };
    }

    // With no script given, starts the loaded one (SKUA_SCRIPT, /script/load,
    // or picked in the Script Loader).
    private async Task<object> StartFromRequest(HttpListenerRequest request)
    {
        string? error = await ScriptFromRequest(request) is { } file
            ? await StartScriptFile(file)
            : await StartLoadedAsync();
        return error is null ? new { started = services.GetRequiredService<IScriptManager>().LoadedScript } : new { error };
    }

    private async Task<object> Scripts(HttpListenerRequest request)
    {
        int limit = int.TryParse(request.QueryString["limit"], out int l) ? Math.Clamp(l, 1, 1000) : 50;
        // The index has "null" (the text) where a script's header leaves a
        // field out: report it as absent.
        static string? Known(string? v) => string.IsNullOrWhiteSpace(v) || v == "null" ? null : v;
        return (await scripts.SearchAsync(request.QueryString["q"], limit, request.QueryString["category"])).Select(s => new
        {
            path = s.FilePath,
            name = Known(s.Name),
            description = Known(s.Description),
            tags = (s.Tags ?? []).Where(t => Known(t) is not null).ToArray(),
            downloaded = s.Downloaded,
            outdated = s.Outdated,
        }).ToList();
    }

    /// <summary>
    /// Loads a script, as the Script Loader's Load button does (it shows there,
    /// ready to start); returns why it failed, if it did. A relative path is a
    /// repository path under Skua/Scripts, fetched if not on disk.
    /// </summary>
    public async Task<string?> LoadScriptFile(string file)
    {
        file = await scripts.ResolveAsync(file);
        if (!File.Exists(file))
            return $"no such file: {file}";
        var manager = services.GetRequiredService<IScriptManager>();
        if (manager.ScriptRunning)
            return $"a script is running ({Path.GetFileName(manager.LoadedScript)}); stop it first";
        manager.SetLoadedScript(file);
        // The Script Loader's own load handling (status line, Start enabled),
        // on the UI thread when there is one.
        services.GetRequiredService<IDispatcherService>().Invoke(() =>
            StrongReferenceMessenger.Default.Send(new LoadScriptMessage(file), (int)MessageChannels.ScriptStatus));
        return null;
    }

    /// <summary>Starts the loaded script; returns why it failed, if it did.</summary>
    public async Task<string?> StartLoadedAsync()
    {
        var manager = services.GetRequiredService<IScriptManager>();
        if (string.IsNullOrEmpty(manager.LoadedScript))
            return "no script loaded: give ?path=<file>, or load one first";
        if (manager.ScriptRunning)
            return null;
        var exception = await manager.StartScript();
        return exception?.ToString();
    }

    /// <summary>Loads and starts a script, stopping any running one first.</summary>
    public async Task<string?> StartScriptFile(string file)
    {
        var manager = services.GetRequiredService<IScriptManager>();
        if (manager.ScriptRunning)
            await manager.StopScript();
        return await LoadScriptFile(file) ?? await StartLoadedAsync();
    }

    private async Task<object> Stop()
    {
        await services.GetRequiredService<IScriptManager>().StopScript();
        return new { stopped = true };
    }

    private object Log(HttpListenerRequest request)
    {
        var type = (request.QueryString["type"] ?? "script").ToLowerInvariant() switch
        {
            "debug" => LogType.Debug,
            "flash" => LogType.Flash,
            _ => LogType.Script,
        };
        int since = int.TryParse(request.QueryString["since"], out int s) ? s : 0;
        var lines = services.GetRequiredService<ILogService>().GetLogs(type);
        return new { type = type.ToString(), total = lines.Count, lines = lines.Skip(since).ToList() };
    }
}
