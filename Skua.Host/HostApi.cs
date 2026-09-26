using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Ruffle;

namespace Skua.Host;

/// <summary>
/// The control API: start/stop scripts, read status and logs. Binds to a
/// local address only; anything that can reach it can run code as the bot.
/// </summary>
public sealed class HostApi(IServiceProvider services, ScriptSync scripts, string prefix)
{
    private readonly HttpListener _listener = new();
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "skua-host");

    public void Start()
    {
        _listener.Prefixes.Add(prefix);
        _listener.Start();
        _ = Task.Run(Loop);   // off any caller's synchronization context
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
                ("POST", "/script/start") => await StartFromRequest(ctx.Request),
                ("POST", "/script/stop") => await Stop(),
                ("GET", "/log") => Log(ctx.Request),
                ("GET", "/scripts") => Scripts(ctx.Request),
                ("POST", "/scripts/update") => await scripts.UpdateScriptsAsync(),
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

    private async Task<object> StartFromRequest(HttpListenerRequest request)
    {
        string? file = request.QueryString["path"];
        if (file is null)
        {
            using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
            string source = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(source))
                return new { error = "give ?path=<file> or the script source as the body" };
            Directory.CreateDirectory(_scratch);
            file = Path.Combine(_scratch, $"api-{DateTime.UtcNow:yyyyMMdd-HHmmss}.cs");
            await File.WriteAllTextAsync(file, source);
        }
        var error = await StartScriptFile(file);
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
    /// Loads and starts a script; returns why it failed, if it did. A relative
    /// path is a repository path under Skua/Scripts, fetched if not on disk.
    /// </summary>
    public async Task<string?> StartScriptFile(string file)
    {
        file = await scripts.ResolveAsync(file);
        if (!File.Exists(file))
            return $"no such file: {file}";
        var manager = services.GetRequiredService<IScriptManager>();
        if (manager.ScriptRunning)
            await manager.StopScript();
        manager.SetLoadedScript(file);
        var exception = await manager.StartScript();
        return exception?.ToString();
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
