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
            result = (method, path) switch
            {
                ("GET", "/status") => Status(),
                ("POST", "/script/load") => await LoadFromRequest(ctx.Request),
                ("POST", "/script/start") => await StartFromRequest(ctx.Request),
                ("POST", "/script/stop") => await Stop(),
                ("GET", "/log") => Log(ctx.Request),
                ("GET", "/scripts") => Scripts(ctx.Request),
                ("POST", "/scripts/update") => await scripts.UpdateScriptsAsync(),
                ("POST", "/scripts/reset") => await scripts.ResetScriptsAsync(),
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

        byte[] body = JsonSerializer.SerializeToUtf8Bytes(result, new JsonSerializerOptions { WriteIndented = true });
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

    private object Status()
    {
        var bridge = services.GetRequiredService<RuffleBridge>();
        var manager = services.GetRequiredService<IScriptManager>();
        object? game = null;
        if (bridge.IsConnected)
        {
            var bot = services.GetRequiredService<IScriptInterface>();
            game = new
            {
                loggedIn = bot.Player.LoggedIn,
                player = bot.Player.Username,
                map = bot.Map.Name,
                cell = bot.Player.Cell,
                hp = bot.Player.Health,
            };
        }
        return new
        {
            instance = SkuaRuntime.Instance,
            bridgeConnected = bridge.IsConnected,
            game,
            script = new { running = manager.ScriptRunning, loaded = manager.LoadedScript },
            scripts = new
            {
                directory = ClientFileSources.SkuaScriptsDIR,
                syncing = scripts.Syncing,
                last = scripts.LastResult,
            },
        };
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

    private object Scripts(HttpListenerRequest request)
    {
        int limit = int.TryParse(request.QueryString["limit"], out int l) ? Math.Clamp(l, 1, 1000) : 50;
        return scripts.Search(request.QueryString["q"], limit).Select(s => new
        {
            path = s.FilePath,
            name = s.Name,
            description = s.Description,
            tags = s.Tags,
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
