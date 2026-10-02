using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using Skua.Linux;

namespace Skua.App.Avalonia;

/// <summary>
/// The tab host's control API, for the web manager (manager/): the tabs, the
/// container's resources and the accounts, plus each tab's own API passed
/// through, so one port reaches all of it. Same rules as the tabs' APIs
/// (Skua.Linux/HostApi.cs): local only unless SKUA_HOST_API_PREFIX says
/// otherwise (http://+:8789/ to publish it, on a LAN address only), and
/// SKUA_API_TOKEN, when set, is required on every request.
///
/// Tabs are numbered from 1, as the accounts are (AQW_USER_1 is tab 1).
///
///   GET    /status                     this host: version, uptime, tabs
///   GET    /tabs                       each tab: account, logged in, its Skua's and page's CPU and memory
///   POST   /tabs[?tab=N]               open a tab (N, else the first free number)
///   POST   /tabs/N/close
///   POST   /tabs/N/restart[?game=1]    restart its Skua; game=1 also reloads its game page
///   POST   /tabs/N/select              show it on the desktop
///   POST   /grid?on=1|0                Grid View
///   ANY    /tabs/N/api/...             tab N's API (/status, /log, /script/..., /scripts, /army/...)
///   POST   /army/...                   an Army Control command to every tab (as /tabs/N/api/army/...)
///   GET    /resources                  load, memory, CPU and memory by kind of process
///   GET    /accounts                   every account; never passwords
///   PUT    /accounts/N                 {user, pass, server, script, autoStart}: add or change tab N's
///                                      account (pass left out keeps it) and apply it
///   DELETE /accounts/N                 remove it and close its tab
///
/// Accounts from the environment (AQW_USER_N) are listed but cannot be
/// changed here.
/// </summary>
public partial class TabHostWindow
{
    private HttpListener? _api;
    private readonly ProcessStats _stats = new();
    private readonly HttpClient _pass = new() { Timeout = TimeSpan.FromMinutes(2) };
    private Dictionary<int, int> _pagePids = new();
    private static readonly DateTime HostStarted = DateTime.UtcNow;

    private static readonly JsonSerializerOptions ApiJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private void StartApi()
    {
        ApiAuth.AddTo(_pass);
        _ = Task.Run(SampleLoop);
        string prefix = SkuaRuntime.Env("SKUA_HOST_API_PREFIX", "http://127.0.0.1:8789/");
        try
        {
            _api = new HttpListener();
            _api.Prefixes.Add(prefix);
            _api.Start();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[tabs] host API not started ({prefix}): {e.Message}");
            _api = null;
            return;
        }
        _ = Task.Run(ApiLoop);
        Console.WriteLine($"[tabs] host API on {prefix}{(ApiAuth.Token is null ? "" : " (token required)")}");
    }

    private void StopApi()
    {
        try { _api?.Stop(); }
        catch { }
    }

    private async Task ApiLoop()
    {
        while (_api is { IsListening: true } api)
        {
            HttpListenerContext ctx;
            try { ctx = await api.GetContextAsync(); }
            catch { return; }
            _ = Task.Run(() => HandleApi(ctx));
        }
    }

    private async Task HandleApi(HttpListenerContext ctx)
    {
        var request = ctx.Request;
        string[] path = request.Url!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? Q(string name) => request.QueryString[name]?.Trim() is { Length: > 0 } v ? v : null;
        int status = 200;
        object? result;
        try
        {
            if (!ApiAuth.Allowed(request))
            {
                await Reply(ctx, 401, new { error = "SKUA_API_TOKEN is set: send it as Authorization: Bearer <token>" });
                return;
            }
            if (path is ["tabs", var number, "api", .. var rest])
            {
                await PassThrough(ctx, number, "/" + string.Join('/', rest));
                return;
            }
            result = (request.HttpMethod, path) switch
            {
                ("GET", ["status"]) => HostStatus(),
                ("GET", ["tabs"]) => await OnUi(TabList),
                ("POST", ["tabs"]) => await OnUi(() => OpenTab(Q("tab"))),
                ("POST", ["tabs", var t, "close"]) => await OnUi(() => WithTab(t, CloseFromApi)),
                ("POST", ["tabs", var t, "restart"]) => await OnUi(() => WithTab(t, tab => RestartTab(tab, Q("game") is "1" or "true"))),
                ("POST", ["tabs", var t, "select"]) => await OnUi(() => WithTab(t, tab => { Select(tab); return Task.FromResult<object>(new { selected = tab.Number + 1 }); })),
                ("POST", ["grid"]) => await OnUi(() => { SetGrid(Q("on") is "1" or "true"); return new { grid = _grid }; }),
                ("POST", ["army", .. var action]) => await ArmyAll(string.Join('/', action), request),
                ("GET", ["resources"]) => ResourceUsage(),
                ("GET", ["accounts"]) => await OnUi(AccountList),
                ("PUT" or "POST", ["accounts", var t]) => await PutAccount(t, request),
                ("DELETE", ["accounts", var t]) => await OnUi(() => DeleteAccount(t)),
                _ => null,
            };
            if (result is null)
                (status, result) = (404, new { error = "not found" });
            else if (result is ApiError error)
                (status, result) = (error.Status, new { error = error.Message });
        }
        catch (Exception e)
        {
            (status, result) = (500, new { error = e.Message });
        }
        await Reply(ctx, status, result);
    }

    private sealed record ApiError(int Status, string Message);

    private static async Task Reply(HttpListenerContext ctx, int status, object? result)
    {
        try
        {
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(result, ApiJson);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(body);
            ctx.Response.Close();
        }
        catch
        {
            // The caller went away.
        }
    }

    // The tabs and everything about them live on the UI thread.
    private static Task<T> OnUi<T>(Func<T> f) => Dispatcher.UIThread.InvokeAsync(f).GetTask();

    private static Task<T> OnUi<T>(Func<Task<T>> f) => Dispatcher.UIThread.InvokeAsync(f);

    private SkuaTab? TabByNumber(string number) =>
        int.TryParse(number, out int n) ? Tabs.FirstOrDefault(t => t.Number == n - 1 && !t.Closed) : null;

    private Task<object> WithTab(string number, Func<SkuaTab, Task<object>> action) =>
        TabByNumber(number) is { } tab ? action(tab) : Task.FromResult<object>(new ApiError(404, $"no tab {number}"));

    // ---- status and tabs ------------------------------------------------------

    private object HostStatus() => new
    {
        version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        started = HostStarted,
        uptimeSeconds = (int)(DateTime.UtcNow - HostStarted).TotalSeconds,
        tabs = Tabs.Count(t => !t.Closed),
        maxTabs = MaxTabs,
        tokenRequired = ApiAuth.Token is not null,
        accountsFile = AccountStore.FilePath,
    };

    private object TabList()
    {
        var samples = _stats.Samples;
        object? Usage(int? pid) => pid is { } p && samples.TryGetValue(p, out var s) ? new { pid = p, cpu = s.Cpu, memoryMb = s.MemoryMb } : null;
        return Tabs.Where(t => !t.Closed).Select(t =>
        {
            int number = t.Number + 1;
            int? skuaPid = t.Process is { HasExited: false } p ? p.Id : null;
            return new
            {
                tab = number,
                title = t.Title,
                account = AccountStore.UserFor(number),
                accountSource = AccountStore.InEnvironment(number) ? "env" : AccountStore.Get(number) is null ? null : "file",
                loggedIn = t.LoggedIn,
                selected = !_grid && t == _selected,
                embedded = t.Xid != 0,
                running = skuaPid is not null,
                started = t.Started,
                restarts = t.Restarts,
                skua = Usage(skuaPid),
                page = Usage(_pagePids.TryGetValue(t.Number, out int page) ? page : null),
            };
        }).ToList();
    }

    private object OpenTab(string? number)
    {
        int? n = null;
        if (number is not null)
        {
            if (!int.TryParse(number, out int asked) || asked is < 1 or > MaxTabs)
                return new ApiError(400, $"tab must be 1 to {MaxTabs}");
            if (Tabs.Any(t => t.Number == asked - 1 && !t.Closed))
                return new ApiError(409, $"tab {asked} is already open");
            n = asked - 1;
        }
        if (AddTab(n) is not { } tab)
            return new ApiError(409, "no room for another tab");
        Layout();
        Console.WriteLine($"[tabs] tab {tab.Number + 1} opened from the host API");
        return new { opened = tab.Number + 1 };
    }

    private async Task<object> CloseFromApi(SkuaTab tab)
    {
        if (Tabs.Count(t => !t.Closed) == 1)
            return new ApiError(409, "this is the last tab");
        await CloseTabAsync(tab);
        return new { closed = tab.Number + 1 };
    }

    /// <summary>
    /// Restarts the tab's Skua (OnExited starts it again, without backoff);
    /// with game, closes and reopens its game page too, which then logs in
    /// with the tab's account as it is now.
    /// </summary>
    private async Task<object> RestartTab(SkuaTab tab, bool game)
    {
        Console.WriteLine($"[tabs] restarting tab {tab.Number + 1}{(game ? " and its game" : "")}");
        var process = tab.Process;
        bool running = process is { HasExited: false };
        if (running)
        {
            tab.RestartRequested = true;
            await Task.Run(() => Stop(process, TimeSpan.FromSeconds(5)));
        }
        if (game)
        {
            try { using var _ = await _quick.SendAsync(Electron(HttpMethod.Delete, tab.Number)); }
            catch (Exception e) { Console.Error.WriteLine($"[tabs] closing game window {tab.Number}: {e.Message}"); }
            tab.GameOpened = false;
            await OpenGame(tab);
        }
        // Not running (it never started, or it is waiting out a backoff): start
        // it now; a pending restart then sees a newer process and skips.
        if (!running)
        {
            tab.RestartDelay = TimeSpan.FromSeconds(2);
            StartProcess(tab);
        }
        return new { restarted = tab.Number + 1, game };
    }

    private async Task<object> ArmyAll(string action, HttpListenerRequest request)
    {
        if (action.Length == 0)
            return new ApiError(404, "not found");
        byte[] body = await ReadBody(request);
        string pathAndQuery = $"/army/{action}{request.Url!.Query}";
        var tabs = await OnUi(() => Live.ToList());
        var sends = new List<(SkuaTab Tab, Task<string?> Reply)>();
        foreach (var tab in tabs)
        {
            // Logins 2 s apart, as Login All does: the server limits them.
            if (action == "login" && sends.Count > 0)
                await Task.Delay(2000);
            sends.Add((tab, Send(tab, pathAndQuery, new ByteArrayContent(body))));
        }
        await Task.WhenAll(sends.Select(s => s.Reply));
        return sends.ToDictionary(s => (s.Tab.Number + 1).ToString(), s => Parse(s.Reply.Result));
    }

    private static JsonElement? Parse(string? text)
    {
        try { return text is null ? null : JsonDocument.Parse(text).RootElement.Clone(); }
        catch { return JsonDocument.Parse(JsonSerializer.Serialize(text)).RootElement.Clone(); }
    }

    private static async Task<byte[]> ReadBody(HttpListenerRequest request)
    {
        if (!request.HasEntityBody)
            return [];
        using var buffer = new MemoryStream();
        await request.InputStream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    // /tabs/N/api/<path>: tab N's own API, as if called directly.
    private async Task PassThrough(HttpListenerContext ctx, string number, string path)
    {
        var tab = await OnUi(() => TabByNumber(number));
        if (tab is null)
        {
            await Reply(ctx, 404, new { error = $"no tab {number}" });
            return;
        }
        if (path.StartsWith("/ui/"))
        {
            await Reply(ctx, 404, new { error = "not found" });   // the tab host's own
            return;
        }
        var request = ctx.Request;
        using var message = new HttpRequestMessage(new HttpMethod(request.HttpMethod), tab.Api + path + request.Url!.Query);
        if (request.HttpMethod is not ("GET" or "HEAD"))
        {
            message.Content = new ByteArrayContent(await ReadBody(request));
            if (request.ContentType is { } type)
                message.Content.Headers.TryAddWithoutValidation("Content-Type", type);
        }
        try
        {
            using var reply = await _pass.SendAsync(message);
            byte[] body = await reply.Content.ReadAsByteArrayAsync();
            ctx.Response.StatusCode = (int)reply.StatusCode;
            ctx.Response.ContentType = reply.Content.Headers.ContentType?.ToString() ?? "application/json";
            await ctx.Response.OutputStream.WriteAsync(body);
            ctx.Response.Close();
        }
        catch (Exception e)
        {
            await Reply(ctx, 502, new { error = $"tab {number} did not answer: {e.Message}" });
        }
    }

    // ---- resources ------------------------------------------------------------

    private async Task SampleLoop()
    {
        while (!_closing)
        {
            try
            {
                _stats.Update();
                _pagePids = await PagePids();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[tabs] sampling processes: {e.Message}");
            }
            await Task.Delay(5000);
        }
    }

    // main.js's GET /instances: each game page's renderer process.
    private async Task<Dictionary<int, int>> PagePids()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_electron}/instances");
            request.Headers.Add("X-Vibeskua", "1");
            using var reply = await _quick.SendAsync(request);
            using var doc = JsonDocument.Parse(await reply.Content.ReadAsStringAsync());
            return doc.RootElement.EnumerateArray()
                .Where(e => e.TryGetProperty("pid", out var p) && p.ValueKind == JsonValueKind.Number)
                .ToDictionary(e => e.GetProperty("instance").GetInt32(), e => e.GetProperty("pid").GetInt32());
        }
        catch
        {
            return _pagePids;
        }
    }

    /// <summary>What each kind of process uses: Skua (the tabs' and this one), the game pages, Electron's GPU process and the rest of it, and the desktop.</summary>
    private object ResourceUsage()
    {
        var samples = _stats.Samples.Values.ToList();
        var skua = Tabs.Select(t => t.Process).Where(p => p is { HasExited: false }).Select(p => p!.Id).Append(Environment.ProcessId).ToHashSet();
        var pages = _pagePids.Values.ToHashSet();
        string Kind(ProcessStats.Sample s) =>
            skua.Contains(s.Pid) ? "skua"
            : pages.Contains(s.Pid) ? "pages"
            : !s.CommandLine.Contains("/electron") ? "desktop"
            : s.CommandLine.Contains("--type=gpu-process") ? "gpu"
            : "electron";
        return new
        {
            time = DateTime.UtcNow,
            cpus = Environment.ProcessorCount,
            load = ProcessStats.Load(),
            memory = ProcessStats.Memory(),
            total = new { cpu = Math.Round(samples.Sum(s => s.Cpu), 1), memoryMb = Math.Round(samples.Sum(s => s.MemoryMb)) },
            kinds = samples.GroupBy(Kind).ToDictionary(g => g.Key, g => new
            {
                processes = g.Count(),
                cpu = Math.Round(g.Sum(s => s.Cpu), 1),
                memoryMb = Math.Round(g.Sum(s => s.MemoryMb)),
            }),
            top = samples.OrderByDescending(s => s.Cpu).Take(8).Select(s => new { s.Pid, s.Name, kind = Kind(s), s.Cpu, s.MemoryMb }),
        };
    }

    // ---- accounts -------------------------------------------------------------

    private object AccountList()
    {
        var file = AccountStore.Load().ToDictionary(a => a.Tab);
        var open = Tabs.Where(t => !t.Closed).ToDictionary(t => t.Number + 1);
        var numbers = Enumerable.Range(1, MaxTabs).Where(n => AccountStore.InEnvironment(n) || file.ContainsKey(n));
        return new
        {
            file = AccountStore.FilePath,
            accounts = numbers.Select(n =>
            {
                bool env = AccountStore.InEnvironment(n);
                var a = env ? null : file[n];
                return new
                {
                    tab = n,
                    user = AccountStore.UserFor(n),
                    source = env ? "env" : "file",
                    editable = !env,
                    hasPassword = env ? (bool?)null : !string.IsNullOrEmpty(a!.Pass),   // the tab host never sees env passwords
                    server = env ? SkuaRuntime.EnvRaw($"AQW_SERVER_{n}") ?? SkuaRuntime.EnvRaw("AQW_SERVER") : a!.Server,
                    script = env ? SkuaRuntime.EnvRaw($"SKUA_SCRIPT_{n}") : a!.Script,
                    autoStart = env ? null : a!.AutoStart,
                    open = open.ContainsKey(n),
                    loggedIn = open.TryGetValue(n, out var tab) && tab.LoggedIn,
                };
            }).ToList(),
        };
    }

    private sealed record AccountInput(string? User, string? Pass, string? Server, string? Script, bool? AutoStart, bool? Open);

    private async Task<object> PutAccount(string number, HttpListenerRequest request)
    {
        if (!int.TryParse(number, out int n) || n is < 1 or > MaxTabs)
            return new ApiError(400, $"tab must be 1 to {MaxTabs}");
        if (AccountStore.InEnvironment(n))
            return new ApiError(409, $"tab {n}'s account is set in the environment (AQW_USER_{n}); change it there");
        AccountInput? input;
        try { input = JsonSerializer.Deserialize<AccountInput>(Encoding.UTF8.GetString(await ReadBody(request)), ApiJson); }
        catch (JsonException e) { return new ApiError(400, $"bad JSON: {e.Message}"); }
        if (input is null || string.IsNullOrWhiteSpace(input.User))
            return new ApiError(400, "user is required");
        var old = AccountStore.Get(n);
        if (old is null && string.IsNullOrEmpty(input.Pass))
            return new ApiError(400, "pass is required for a new account");

        var saved = AccountStore.Put(new AccountStore.Account
        {
            Tab = n,
            User = input.User.Trim(),
            Pass = input.Pass,
            Server = NullIfBlank(input.Server),
            Script = NullIfBlank(input.Script),
            AutoStart = input.AutoStart,
        });
        bool login = old is null || old.User != saved.User || old.Pass != saved.Pass || old.Server != saved.Server;
        bool scriptChanged = old is not null && (old.Script != saved.Script || old.AutoStart != saved.AutoStart);
        Console.WriteLine($"[tabs] tab {n}: account {(old is null ? "added" : "changed")} from the host API");

        // Apply it: open its tab, or restart the open one when the login
        // changed. A new script only applies at the tab's next start, rather
        // than stop the one running.
        string applied = await OnUi(async () =>
        {
            if (TabByNumber(number) is not { } tab)
            {
                if (input.Open == false)
                    return "saved";
                if (AddTab(n - 1) is null)
                    return "saved (no room for another tab)";
                Layout();
                return "opened";
            }
            if (!login)
                return scriptChanged ? "saved; the script applies when the tab restarts" : "unchanged";
            await RestartTab(tab, game: true);
            return "restarted";
        });
        return new { tab = n, user = saved.User, applied };
    }

    private async Task<object> DeleteAccount(string number)
    {
        if (!int.TryParse(number, out int n) || n is < 1 or > MaxTabs)
            return new ApiError(400, $"tab must be 1 to {MaxTabs}");
        if (AccountStore.InEnvironment(n))
            return new ApiError(409, $"tab {n}'s account is set in the environment (AQW_USER_{n}); remove it there");
        if (!AccountStore.Remove(n))
            return new ApiError(404, $"tab {n} has no account in the accounts file");
        Console.WriteLine($"[tabs] tab {n}: account removed from the host API");
        string applied = "removed";
        if (TabByNumber(number) is { } tab)
        {
            // Its tab goes too; the last tab stays, logged out.
            if (Tabs.Count(t => !t.Closed) > 1)
            {
                await CloseTabAsync(tab);
                applied = "removed; tab closed";
            }
            else
            {
                await RestartTab(tab, game: true);
                applied = "removed; the last tab restarted without it";
            }
        }
        return new { tab = n, applied };
    }
}
