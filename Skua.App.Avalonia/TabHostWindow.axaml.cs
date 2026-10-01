using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.ViewModels;
using Skua.Linux;

namespace Skua.App.Avalonia;

/// <summary>One tab: an AQW account with its own Skua process and game window.</summary>
public sealed class SkuaTab(int number) : ObservableObject
{
    private string _title = $"Skua {number + 1}";
    private bool _isSelected;

    /// <summary>Its instance number (SKUA_INSTANCE): ports and game window go by it.</summary>
    public int Number { get; } = number;

    public string Title { get => _title; set => SetProperty(ref _title, value); }
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    internal Process? Process;
    internal string Api = "";
    internal ulong Xid;
    internal (int X, int Y, int W, int H) Placed;
    internal bool? Throttled;
    internal bool? Grid;
    internal bool LoggedIn;
    internal bool GameOpened;
    internal bool Closed;
    internal DateTime Started;
    internal TimeSpan RestartDelay = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Skua.App.WPF's TabbedHostWindow on Linux. Each tab is its own Skua
/// (this program with --tab-child, SKUA_INSTANCE=N) with its own game window
/// from main.js (/instances/N): this window asks main.js for the game, starts
/// the Skua, and re-parents that Skua's main window (menu over its game) into
/// the area under the tabs, the way WPF placed the child windows there.
///
/// Tabs are told what to do through their control APIs (Skua.Linux/ArmyApi.cs)
/// where WPF posted window messages: Army Control sends every tab the same
/// command, Grid View shows every tab's game at once (menus hidden), and tabs
/// not on screen are parked out of view and slowed to 2 frames a second.
///
/// Instance N's Skua listens on the bridge and API ports + 10*N; instance 0
/// keeps SKUA_BRIDGE_PREFIX / SKUA_API_PREFIX, SKUA_SCRIPT and the script sync.
/// </summary>
public partial class TabHostWindow : Window
{
    /// <summary>Tabs unless SKUA_TABS=0 (or the game is not embedded, SKUA_EMBED_GAME=0).</summary>
    public static bool Enabled =>
        SkuaRuntime.EnvRaw("SKUA_TABS")?.ToLowerInvariant() is not ("0" or "false" or "no" or "off")
        && SkuaRuntime.EnvRaw("SKUA_EMBED_GAME")?.ToLowerInvariant() is not ("0" or "false" or "no" or "off");

    /// <summary>
    /// How many tabs to open at start: one per configured account (the
    /// highest N with AQW_USER_N set; main.js logs each in), or SKUA_TABS=N
    /// if that is more; at least one.
    /// </summary>
    private static int InitialTabs
    {
        get
        {
            int accounts = Enumerable.Range(1, MaxTabs).LastOrDefault(n => SkuaRuntime.EnvRaw($"AQW_USER_{n}") is not null);
            int asked = int.TryParse(SkuaRuntime.EnvRaw("SKUA_TABS"), out int t) ? t : 1;
            return Math.Clamp(Math.Max(accounts, asked), 1, MaxTabs);
        }
    }

    private const int MaxTabs = 50;

    public ObservableCollection<SkuaTab> Tabs { get; } = new();

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(90) };
    private readonly HttpClient _quick = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly string _electron = SkuaRuntime.Env("SKUA_BRIDGE_ORIGINS", "http://127.0.0.1:8770").Split(',')[0].TrimEnd('/');
    private readonly CancellationTokenSource _cts = new();
    private readonly List<IDisposable> _signals = new();
    private readonly Dictionary<string, bool> _options = new();
    private SkuaTab? _selected;
    private bool _grid;
    private bool _closing;
    private bool _polling;
    private IntPtr _display;
    private ulong _hostXid;
    private string _lastJumpMap = "", _lastJumpCell = "", _lastJumpPlayer = "", _lastQuestId = "", _lastQuestItem = "";

    public TabHostWindow()
    {
        InitializeComponent();
        DataContext = this;
        Title = "VibeSkua";

        HostArea.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
                Layout();
        };
        Opened += (_, _) =>
        {
            _display = X.XOpenDisplay(null);
            _hostXid = TryGetPlatformHandle()?.Handle is { } h ? (ulong)h : 0;
            for (int i = 0; i < InitialTabs; i++)
                AddTab();
            DispatcherTimer.Run(() => { _ = Poll(); return !_closing; }, TimeSpan.FromSeconds(1.5));
            DispatcherTimer.Run(() => { PumpFocus(); return !_closing; }, TimeSpan.FromMilliseconds(30));
        };
        Closing += (_, _) => StopAll();
        WindowPlacement.FillScreenOnChange(this);
        foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGHUP })
            _signals.Add(PosixSignalRegistration.Create(signal, _ => StopAll()));
        AppDomain.CurrentDomain.ProcessExit += (_, _) => StopAll();

        // Load Script to All (the Script Repo's Load) and the Army Scheduler,
        // as WPF's host caught them.
        StrongReferenceMessenger.Default.Register<TabHostWindow, LoadScriptMessage, int>(this, (int)MessageChannels.ScriptStatus, (w, m) => w.BroadcastLoad(m.Path));
        StrongReferenceMessenger.Default.Register<TabHostWindow, ScriptSchedulerViewModel.ArmySchedulerMessage>(this, (w, m) => w.OnArmyScheduler(m));
        StrongReferenceMessenger.Default.Register<TabHostWindow, ScriptSchedulerViewModel.ArmySchedulerStopMessage>(this, (w, m) => w.OnArmySchedulerStop(m));
    }

    // ---- tabs ------------------------------------------------------------

    private void AddTab()
    {
        if (Tabs.Count >= MaxTabs)
            return;
        int n = Enumerable.Range(0, MaxTabs).First(i => Tabs.All(t => t.Number != i));
        var tab = new SkuaTab(n) { Api = LocalBase(PrefixFor("SKUA_API_PREFIX", "http://127.0.0.1:8791/", n)) };
        int at = Tabs.TakeWhile(t => t.Number < n).Count();
        Tabs.Insert(at, tab);
        _ = OpenGame(tab);
        StartProcess(tab);
        Select(tab);
    }

    private void Select(SkuaTab tab)
    {
        if (_grid)
            SetGrid(false);
        _selected = tab;
        foreach (var t in Tabs)
            t.IsSelected = t == tab;
        Layout();
    }

    private async void CloseTab(SkuaTab tab)
    {
        if (Tabs.Count == 1)
        {
            App.Service<IDialogService>().ShowMessageBox("This is the last tab; close VibeSkua instead.", "Close Tab");
            return;
        }
        tab.Closed = true;
        int index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        if (_selected == tab || _selected is null)
            Select(Tabs[Math.Min(index, Tabs.Count - 1)]);
        else
            Layout();
        Console.WriteLine($"[tabs] closing tab {tab.Number + 1}");
        // Skua hands the game window back as it stops; then main.js closes it.
        await Task.Run(() => Stop(tab.Process, TimeSpan.FromSeconds(5)));
        try { await _quick.SendAsync(Electron(HttpMethod.Delete, tab.Number)); }
        catch (Exception e) { Console.Error.WriteLine($"[tabs] closing game window {tab.Number}: {e.Message}"); }
    }

    private async Task OpenGame(SkuaTab tab)
    {
        try
        {
            using var reply = await _quick.SendAsync(Electron(HttpMethod.Post, tab.Number));
            tab.GameOpened = reply.IsSuccessStatusCode;
            if (!tab.GameOpened)
                Console.Error.WriteLine($"[tabs] main.js would not open game window {tab.Number} ({(int)reply.StatusCode})");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[tabs] opening game window {tab.Number}: {e.Message}");
        }
    }

    private HttpRequestMessage Electron(HttpMethod method, int n)
    {
        var request = new HttpRequestMessage(method, $"{_electron}/instances/{n}");
        request.Headers.Add("X-Vibeskua", "1");
        return request;
    }

    // ---- the tabs' Skua processes -------------------------------------------

    /// <summary>Instance N's listen prefix: the configured one for 0, its port + 10*N otherwise.</summary>
    private static string PrefixFor(string variable, string fallback, int n)
    {
        string prefix = SkuaRuntime.Env(variable, fallback);
        return n == 0 ? prefix : Regex.Replace(prefix, @":(\d+)/?$", m => $":{int.Parse(m.Groups[1].Value) + 10 * n}/");
    }

    // http://+:8791/ listens everywhere; this process reaches it on 127.0.0.1.
    private static string LocalBase(string prefix) => Regex.Replace(prefix, @"://[+*]:", "://127.0.0.1:").TrimEnd('/');

    private void StartProcess(SkuaTab tab)
    {
        int n = tab.Number;
        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (Path.GetFileNameWithoutExtension(psi.FileName) == "dotnet")
            psi.ArgumentList.Add(typeof(TabHostWindow).Assembly.Location);
        psi.ArgumentList.Add("--tab-child");
        psi.Environment["SKUA_INSTANCE"] = n.ToString();
        psi.Environment["SKUA_TAB_HOST_PID"] = Environment.ProcessId.ToString();
        psi.Environment["SKUA_BRIDGE_PREFIX"] = PrefixFor("SKUA_BRIDGE_PREFIX", "http://127.0.0.1:8790/", n);
        psi.Environment["SKUA_API_PREFIX"] = PrefixFor("SKUA_API_PREFIX", "http://127.0.0.1:8791/", n);
        // One sync (the first tab's) is enough: they share the Scripts folder.
        if (n > 0)
            psi.Environment["SKUA_SCRIPT_SYNC"] = "off";
        // Tab N's script: SKUA_SCRIPT_N, else plain SKUA_SCRIPT (every tab's);
        // SKUA_SCRIPT_N=none gives that tab none. Started once logged in if
        // SKUA_SCRIPT_AUTO_START_N, else SKUA_SCRIPT_AUTO_START, says so.
        string? script = SkuaRuntime.EnvRaw($"SKUA_SCRIPT_{n + 1}") ?? SkuaRuntime.EnvRaw("SKUA_SCRIPT");
        if (script?.ToLowerInvariant() is "none" or "off" or "-")
            script = null;
        string? autoStart = SkuaRuntime.EnvRaw($"SKUA_SCRIPT_AUTO_START_{n + 1}") ?? SkuaRuntime.EnvRaw("SKUA_SCRIPT_AUTO_START");
        SetOrRemove(psi, "SKUA_SCRIPT", script);
        SetOrRemove(psi, "SKUA_SCRIPT_AUTO_START", autoStart);
        // CoreBots room: SKUA_ROOM_NUMBER_N, else SKUA_ROOM_NUMBER (every tab's).
        SetOrRemove(psi, "SKUA_ROOM_NUMBER", SkuaRuntime.EnvRaw($"SKUA_ROOM_NUMBER_{n + 1}") ?? SkuaRuntime.EnvRaw("SKUA_ROOM_NUMBER"));

        Process process;
        try
        {
            process = Process.Start(psi)!;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[tabs] could not start Skua for tab {n + 1}: {e.Message}");
            return;
        }
        string tag = n == 0 ? "" : $"[tab {n + 1}] ";
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) Console.WriteLine(tag + e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine(tag + e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => Dispatcher.UIThread.Post(() => OnExited(tab, process));
        tab.Process = process;
        tab.Xid = 0;
        tab.Grid = null;
        tab.Throttled = null;
        tab.Started = DateTime.UtcNow;
        Console.WriteLine($"[tabs] tab {n + 1}: Skua pid {process.Id}, api {tab.Api}");
    }

    private static void SetOrRemove(ProcessStartInfo psi, string name, string? value)
    {
        if (value is null)
            psi.Environment.Remove(name);
        else
            psi.Environment[name] = value;
    }

    // Restart a tab's Skua that died, as main.js restarts the single one;
    // back off only if it keeps dying young.
    private void OnExited(SkuaTab tab, Process process)
    {
        if (tab.Closed || _closing || tab.Process != process)
            return;
        tab.Xid = 0;
        tab.RestartDelay = DateTime.UtcNow - tab.Started > TimeSpan.FromMinutes(1)
            ? TimeSpan.FromSeconds(2)
            : TimeSpan.FromSeconds(Math.Min(tab.RestartDelay.TotalSeconds * 2, 60));
        Console.Error.WriteLine($"[tabs] tab {tab.Number + 1}: Skua exited ({process.ExitCode}); restarting in {tab.RestartDelay.TotalSeconds}s");
        Layout();
        DispatcherTimer.RunOnce(() =>
        {
            if (!tab.Closed && !_closing)
                StartProcess(tab);
        }, tab.RestartDelay);
    }

    private void StopAll()
    {
        if (_closing)
            return;
        _closing = true;
        _cts.Cancel();
        var processes = Tabs.Select(t => t.Process).ToList();
        Parallel.ForEach(processes, p => Stop(p, TimeSpan.FromSeconds(4)));
    }

    // SIGTERM first: Skua then hands its game window back to the desktop.
    private static void Stop(Process? process, TimeSpan grace)
    {
        if (process is null)
            return;
        try
        {
            if (process.HasExited)
                return;
            Kill(process.Id, 15);
            if (!process.WaitForExit(grace))
                process.Kill();
        }
        catch
        {
        }
    }

    /// <summary>
    /// In a tab's Skua: exit (as on a stop signal) once the tab host is gone,
    /// rather than stay behind with nothing showing it.
    /// </summary>
    public static void WatchHost()
    {
        if (!int.TryParse(SkuaRuntime.EnvRaw("SKUA_TAB_HOST_PID"), out int host))
            return;
        _ = Task.Run(async () =>
        {
            while (Directory.Exists($"/proc/{host}"))
                await Task.Delay(2000);
            Console.Error.WriteLine("[host] the tab host is gone; stopping");
            Kill(Environment.ProcessId, 15);
        });
    }

    [DllImport("libc", EntryPoint = "kill")]
    private static extern int Kill(int pid, int signal);

    // ---- keeping each tab's window in place ------------------------------------

    private async Task Poll()
    {
        if (_polling || _closing)
            return;
        _polling = true;
        try
        {
            foreach (var tab in Tabs.ToList())
            {
                if (tab.Closed)
                    continue;
                if (!tab.GameOpened)
                    await OpenGame(tab);
                await UpdateStatus(tab);
                if (tab.Xid == 0 || ParentOf(tab.Xid) != _hostXid)
                    await FindWindow(tab);
            }
            Layout();
        }
        finally
        {
            _polling = false;
        }
    }

    private async Task UpdateStatus(SkuaTab tab)
    {
        try
        {
            using var doc = JsonDocument.Parse(await _quick.GetStringAsync($"{tab.Api}/status"));
            var game = doc.RootElement.GetProperty("game");
            bool loggedIn = game.ValueKind == JsonValueKind.Object && game.GetProperty("loggedIn").GetBoolean();
            string? player = loggedIn ? game.GetProperty("player").GetString() : null;
            tab.LoggedIn = loggedIn;
            tab.Title = string.IsNullOrWhiteSpace(player) ? $"Skua {tab.Number + 1}" : player!;
        }
        catch
        {
            tab.LoggedIn = false;
        }
    }

    private async Task FindWindow(SkuaTab tab)
    {
        try
        {
            var reply = await _quick.GetFromJsonAsync<WindowReply>($"{tab.Api}/ui/window");
            if (reply?.Xid is { } text && ulong.TryParse(text, out ulong xid) && xid != 0)
                Embed(tab, xid);
        }
        catch
        {
            // Not up yet.
        }
    }

    private sealed record WindowReply(string? Xid);

    private void Embed(SkuaTab tab, ulong xid)
    {
        if (_display == IntPtr.Zero || _hostXid == 0)
            return;
        // Take it from the window manager (which unmanages it on unmap), then
        // make it a child of ours, as GameEmbed does with the game.
        X.XUnmapWindow(_display, xid);
        X.XSync(_display, false);
        Thread.Sleep(150);
        X.XReparentWindow(_display, xid, _hostXid, 0, 0);
        // Should this window go, the tab's window returns to the desktop
        // rather than die with it (the tab's Skua then stops: WatchHost).
        GameEmbed.AddToAvaloniaSaveSet(xid);
        X.XSelectInput(_display, xid, X.EnterWindowMask | X.LeaveWindowMask);
        X.XMapWindow(_display, xid);
        X.XSync(_display, false);
        tab.Xid = xid;
        tab.Placed = default;
        tab.Grid = null;
        tab.Throttled = null;
        Console.WriteLine($"[tabs] tab {tab.Number + 1}: window 0x{xid:x} embedded");
        Layout();
    }

    private ulong ParentOf(ulong window)
    {
        if (_display == IntPtr.Zero || X.XQueryTree(_display, window, out _, out ulong parent, out IntPtr children, out _) == 0)
            return 0;
        if (children != IntPtr.Zero)
            X.XFree(children);
        return parent;
    }

    /// <summary>
    /// The selected tab over the area under the tabs, the others parked just
    /// outside it (still drawn, so their games keep running; slowed); in Grid
    /// View all of them in a grid.
    /// </summary>
    private void Layout()
    {
        if (_display == IntPtr.Zero || _closing)
            return;
        LoadingIndicator.IsVisible = _grid ? Tabs.Any(t => t.Xid == 0) : _selected is null || _selected.Xid == 0;
        double scale = RenderScaling;
        if (HostArea.TranslatePoint(new Point(0, 0), this) is not { } origin)
            return;
        int x = (int)(origin.X * scale), y = (int)(origin.Y * scale);
        int w = Math.Max(1, (int)(HostArea.Bounds.Width * scale)), h = Math.Max(1, (int)(HostArea.Bounds.Height * scale));

        var live = Tabs.Where(t => t.Xid != 0).ToList();
        if (_grid)
        {
            int count = Math.Max(1, live.Count);
            int cols = (int)Math.Ceiling(Math.Sqrt(count));
            int rows = (int)Math.Ceiling((double)count / cols);
            int cellW = w / cols, cellH = h / rows;
            for (int i = 0; i < live.Count; i++)
            {
                Place(live[i], x + i % cols * cellW, y + i / cols * cellH, cellW, cellH);
                SetThrottle(live[i], false);
                SetChildGrid(live[i], true);
            }
        }
        else
        {
            foreach (var tab in live)
            {
                bool shown = tab == _selected;
                Place(tab, shown ? x : -w - 100, y, w, h);
                SetThrottle(tab, !shown);
                SetChildGrid(tab, false);
                if (shown)
                    X.XRaiseWindow(_display, tab.Xid);
            }
        }
        X.XFlush(_display);
    }

    private void Place(SkuaTab tab, int x, int y, int w, int h)
    {
        if (tab.Placed == (x, y, w, h))
            return;
        tab.Placed = (x, y, w, h);
        X.XMoveResizeWindow(_display, tab.Xid, x, y, (uint)w, (uint)h);
    }

    private void SetThrottle(SkuaTab tab, bool on)
    {
        if (tab.Throttled == on)
            return;
        tab.Throttled = on;
        _ = Send(tab, $"/army/throttle?on={(on ? 1 : 0)}");
    }

    private void SetChildGrid(SkuaTab tab, bool on)
    {
        if (tab.Grid == on)
            return;
        tab.Grid = on;
        _ = Send(tab, $"/ui/grid?on={(on ? 1 : 0)}");
    }

    private void SetGrid(bool on)
    {
        _grid = on;
        GridButton.IsChecked = on;
        foreach (var t in Tabs)
            t.IsSelected = !on && t == _selected;
        Layout();
    }

    // Keyboard focus follows the pointer into a tab's window: the window
    // manager only focuses this one, the window it manages. Inside the tab,
    // its GameEmbed passes focus on to the game. On the UI thread, like every
    // other use of this X connection (Xlib is not thread-safe).
    private readonly IntPtr _event = Marshal.AllocHGlobal(256);

    private void PumpFocus()
    {
        const int NotifyVirtual = 1, NotifyInferior = 2, NotifyNonlinearVirtual = 4;
        IntPtr display = _display;
        if (display == IntPtr.Zero || _closing)
            return;
        while (X.XPending(display) > 0)
        {
            X.XNextEvent(display, _event);
            int type = Marshal.ReadInt32(_event);
            ulong window = (ulong)Marshal.ReadInt64(_event, 32);
            int detail = Marshal.ReadInt32(_event, 84);
            // Grabs (a click in the game) report crossings without the
            // pointer moving; only real movement moves focus.
            if (detail == NotifyInferior || Marshal.ReadInt32(_event, 80) != 0)
                continue;
            // Straight into the tab's game (a window inside it): the tab's own
            // GameEmbed focuses the game; focusing the tab here as well would
            // race it and could take the keys from the game.
            if (type == X.EnterNotify && detail is NotifyVirtual or NotifyNonlinearVirtual)
                continue;
            if (type == X.EnterNotify)
                X.XSetInputFocus(display, window, X.RevertToParent, 0);
            else if (type == X.LeaveNotify && _hostXid != 0)
                X.XSetInputFocus(display, _hostXid, X.RevertToParent, 0);
        }
        X.XFlush(display);
    }

    // ---- talking to the tabs ------------------------------------------------------

    private IEnumerable<SkuaTab> Live => Tabs.Where(t => !t.Closed && t.Process is { HasExited: false }).ToList();

    private async Task<string?> Send(SkuaTab tab, string pathAndQuery, HttpContent? body = null)
    {
        try
        {
            using var reply = await _http.PostAsync(tab.Api + pathAndQuery, body);
            string text = await reply.Content.ReadAsStringAsync();
            if (text.Contains("\"error\""))
                Console.Error.WriteLine($"[tabs] tab {tab.Number + 1} {pathAndQuery.Split('?')[0]}: {text.ReplaceLineEndings(" ")}");
            return text;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[tabs] tab {tab.Number + 1} {pathAndQuery.Split('?')[0]}: {e.Message}");
            return null;
        }
    }

    private Task Broadcast(string pathAndQuery, Func<HttpContent?>? body = null) =>
        Task.WhenAll(Live.Select(t => Send(t, pathAndQuery, body?.Invoke())));

    private static string Q(string value) => Uri.EscapeDataString(value);

    private void BroadcastLoad(string path)
    {
        Console.WriteLine($"[tabs] loading {path} in every tab");
        _ = Broadcast($"/script/load?path={Q(path)}");
    }

    private void OnArmyScheduler(ScriptSchedulerViewModel.ArmySchedulerMessage m)
    {
        m.Handled = true;
        var dialogs = App.Service<IDialogService>();
        int notLoggedIn = Live.Count(t => !t.LoggedIn);
        if (notLoggedIn > 0)
        {
            dialogs.ShowMessageBox($"Cannot start Army Scheduler! {notLoggedIn} account(s) are not logged in. Please log them in first.", "Army Scheduler Error");
            return;
        }
        string payload = JsonSerializer.Serialize(m.Queue.Select(x => new ScriptSchedulerViewModel.SavedScriptItem { Path = x.Path, Id = x.Id, Name = x.Name }).ToList());
        _ = Broadcast("/army/scheduler", () => new StringContent(payload, System.Text.Encoding.UTF8, "application/json"));
        dialogs.ShowMessageBox("Army Scheduler payload has been broadcasted. All active accounts will now seamlessly rebuild their queues and begin executing the playlist.", "Army Scheduler Started");
    }

    private void OnArmySchedulerStop(ScriptSchedulerViewModel.ArmySchedulerStopMessage m)
    {
        m.Handled = true;
        _ = Broadcast("/army/scheduler/stop");
        App.Service<IDialogService>().ShowMessageBox("Stop signal has been successfully broadcasted. All accounts are now halting their schedulers.", "Army Scheduler Stopped");
    }

    // ---- the tab strip ------------------------------------------------------------

    private void Tab_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.DataContext is SkuaTab tab)
            Select(tab);
    }

    private void CloseTab_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as Control)?.DataContext is SkuaTab tab)
            CloseTab(tab);
    }

    private void AddTab_Click(object? sender, RoutedEventArgs e) => AddTab();

    private void Grid_Click(object? sender, RoutedEventArgs e)
    {
        bool on = GridButton.IsChecked == true;
        if (!on && _selected is null && Tabs.Count > 0)
            _selected = Tabs[0];
        SetGrid(on);
    }

    // ---- Army Control -----------------------------------------------------------

    private async void StartAll_Click(object? sender, RoutedEventArgs e)
    {
        MenuStartScripts.IsEnabled = MenuStopScripts.IsEnabled = false;
        await Broadcast("/army/start");
        await Task.Delay(2000);
        MenuStartScripts.IsEnabled = MenuStopScripts.IsEnabled = true;
    }

    private async void StopAll_Click(object? sender, RoutedEventArgs e)
    {
        MenuStartScripts.IsEnabled = MenuStopScripts.IsEnabled = false;
        await Broadcast("/army/stop");
        await Task.Delay(4000);   // let the scripts stop gracefully
        MenuStartScripts.IsEnabled = MenuStopScripts.IsEnabled = true;
    }

    // As WPF: the Script Repo; its Load (LoadScriptMessage) goes to every tab.
    private void LoadScriptAll_Click(object? sender, RoutedEventArgs e)
    {
        var windows = App.Service<IWindowService>();
        windows.RegisterManagedWindow("Script Repo", App.Service<ScriptRepoViewModel>());
        windows.ShowManagedWindow("Script Repo");
    }

    private void ArmyScheduler_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var windows = App.Service<IWindowService>();
            windows.RegisterManagedWindow("Scheduler", App.Service<ScriptSchedulerViewModel>());
            windows.RegisterManagedWindow("Script Repo", App.Service<ScriptRepoViewModel>());
            windows.ShowManagedWindow("Scheduler");
        }
        catch (Exception ex)
        {
            App.Service<IDialogService>().ShowMessageBox(ex.ToString(), "Error Opening Scheduler");
        }
    }

    private async void LoginAll_Click(object? sender, RoutedEventArgs e)
    {
        string header = MenuLoginAll.Header?.ToString() ?? "Login All Clients";
        MenuLoginAll.IsEnabled = false;
        var tabs = Live.ToList();
        for (int i = 0; i < tabs.Count; i++)
        {
            MenuLoginAll.Header = $"Logging in... ({i + 1}/{tabs.Count})";
            _ = Send(tabs[i], "/army/login");
            await Task.Delay(2000);
        }
        MenuLoginAll.Header = header;
        MenuLoginAll.IsEnabled = true;
    }

    private void LogoutAll_Click(object? sender, RoutedEventArgs e) => _ = Broadcast("/army/logout");

    private void JumpMap_Click(object? sender, RoutedEventArgs e)
    {
        var vm = new InputDialogViewModel("Jump Army to Map / Cell", "Enter target location:", "Map (e.g., yulgar-829472)", "Cell (e.g., Enter)", false)
        {
            DialogTextInput = _lastJumpMap,
            SecondTextInput = _lastJumpCell,
        };
        if (App.Service<IDialogService>().ShowDialog(vm) != true)
            return;
        string map = vm.DialogTextInput?.Trim() ?? "", cell = vm.SecondTextInput?.Trim() ?? "";
        if (map.Length == 0 && cell.Length == 0)
            return;
        (_lastJumpMap, _lastJumpCell) = (map, cell);
        _ = Broadcast($"/army/jump?map={Q(map)}&cell={Q(cell)}");
    }

    private void JumpPlayer_Click(object? sender, RoutedEventArgs e)
    {
        var vm = new InputDialogViewModel("Jump Army to Player", "Enter target player username:", "e.g., Artix", false)
        {
            DialogTextInput = _lastJumpPlayer,
        };
        if (App.Service<IDialogService>().ShowDialog(vm) != true || vm.DialogTextInput?.Trim() is not { Length: > 0 } player)
            return;
        _lastJumpPlayer = player;
        _ = Broadcast($"/army/goto?player={Q(player)}");
    }

    private void AcceptQuest_Click(object? sender, RoutedEventArgs e)
    {
        var vm = new InputDialogViewModel("Accept Quest (Army)", "Enter quest to accept:", "Quest ID (e.g., 1907)", "Item to accept (optional, e.g., 40816)", false)
        {
            DialogTextInput = _lastQuestId,
            SecondTextInput = _lastQuestItem,
        };
        if (App.Service<IDialogService>().ShowDialog(vm) != true || vm.DialogTextInput?.Trim() is not { Length: > 0 } quest)
            return;
        (_lastQuestId, _lastQuestItem) = (quest, vm.SecondTextInput?.Trim() ?? "");
        _ = Broadcast($"/army/quest?id={Q(quest)}&item={Q(_lastQuestItem)}");
    }

    // Misc Options: each toggle is set in every tab.
    private void Option_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string name } item)
            return;
        bool value = !_options.GetValueOrDefault(name);
        _options[name] = value;
        // After any toggling the menu item does itself.
        Dispatcher.UIThread.Post(() => item.IsChecked = value);
        _ = Broadcast($"/army/option?name={name}&value={value.ToString().ToLowerInvariant()}");
    }

    protected override void OnClosed(EventArgs e)
    {
        StopAll();
        StrongReferenceMessenger.Default.UnregisterAll(this);
        foreach (var s in _signals)
            s.Dispose();
        base.OnClosed(e);
    }

    private static class X
    {
        private const string Lib = "libX11.so.6";
        public const long EnterWindowMask = 1L << 4;
        public const long LeaveWindowMask = 1L << 5;
        public const int EnterNotify = 7;
        public const int LeaveNotify = 8;
        public const int RevertToParent = 2;

        [DllImport(Lib)] public static extern IntPtr XOpenDisplay(string? name);
        [DllImport(Lib)] public static extern int XReparentWindow(IntPtr display, ulong window, ulong parent, int x, int y);
        [DllImport(Lib)] public static extern int XMapWindow(IntPtr display, ulong window);
        [DllImport(Lib)] public static extern int XUnmapWindow(IntPtr display, ulong window);
        [DllImport(Lib)] public static extern int XRaiseWindow(IntPtr display, ulong window);
        [DllImport(Lib)] public static extern int XMoveResizeWindow(IntPtr display, ulong window, int x, int y, uint width, uint height);
        [DllImport(Lib)] public static extern int XSelectInput(IntPtr display, ulong window, long mask);
        [DllImport(Lib)] public static extern int XSetInputFocus(IntPtr display, ulong window, int revertTo, ulong time);
        [DllImport(Lib)] public static extern int XSync(IntPtr display, bool discard);
        [DllImport(Lib)] public static extern int XFlush(IntPtr display);
        [DllImport(Lib)] public static extern int XPending(IntPtr display);
        [DllImport(Lib)] public static extern int XNextEvent(IntPtr display, IntPtr ev);
        [DllImport(Lib)] public static extern int XFree(IntPtr data);
        [DllImport(Lib)] public static extern int XQueryTree(IntPtr display, ulong window, out ulong root, out ulong parent, out IntPtr children, out uint count);
    }
}
