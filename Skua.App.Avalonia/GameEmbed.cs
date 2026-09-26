using System.Net.Http.Json;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Skua.Linux;

namespace Skua.App.Avalonia;

/// <summary>
/// Puts the game inside Skua's main window, under the menu, as Skua.App.WPF
/// hosted the Flash control there. The game is the vibeskua-web Electron
/// window: main.js serves its X11 id at /game-window, and this re-parents
/// that window into ours (X11's equivalent of the SetParent the WPF tabbed
/// host uses), keeps it sized to the game area, and gives it keyboard focus
/// while the pointer is over it.
///
/// When Skua stops (a stop signal, or its window closing) the game window is
/// handed back to the desktop first, and a restarted Skua embeds it again. It
/// is also in the save-set of Avalonia's X connection, which on a real X
/// server returns it to the desktop if Skua dies outright (WSLg's XWayland was
/// seen not to); main.js opens a new game window if it is destroyed anyway. If no game window turns up, <see cref="Failed"/> fires and
/// the main window falls back to a bar above the game.
/// </summary>
public sealed class GameEmbed : IDisposable
{
    private readonly Window _window;
    private readonly Control _area;
    private readonly string _endpoint;
    private readonly CancellationTokenSource _cts = new();
    private IntPtr _display;
    private ulong _host;
    private ulong _game;
    private (int X, int Y, int W, int H) _placed;
    private readonly List<IDisposable> _signals = new();
    private readonly object _releaseLock = new();

    public GameEmbed(Window window, Control area)
    {
        _window = window;
        _area = area;
        string origin = SkuaRuntime.Env("SKUA_BRIDGE_ORIGINS", "http://127.0.0.1:8770").Split(',')[0].TrimEnd('/');
        _endpoint = origin + "/game-window";
    }

    /// <summary>Raised on the UI thread if the game window never appears.</summary>
    public event Action? Failed;

    /// <summary>Raised on the UI thread once the game is inside the window.</summary>
    public event Action? Embedded;

    public void Start()
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            Failed?.Invoke();
            return;
        }
        _area.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.BoundsProperty)
                Place();
        };
        // Hand the game back before anything tears Skua's window down: on the
        // stop signal itself (Avalonia's shutdown on ProcessExit destroys the
        // window, and the game with it, before a ProcessExit handler of ours).
        foreach (var signal in new[] { System.Runtime.InteropServices.PosixSignal.SIGTERM, System.Runtime.InteropServices.PosixSignal.SIGINT, System.Runtime.InteropServices.PosixSignal.SIGHUP })
            _signals.Add(System.Runtime.InteropServices.PosixSignalRegistration.Create(signal, _ => Release()));
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Release();
        _ = Task.Run(Watch);
        _ = Task.Run(FocusLoop);
    }

    // Find the game window and keep it embedded: Electron can recreate it,
    // and a Skua restart starts from scratch.
    private async Task Watch()
    {
        using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(3) };
        DateTime giveUp = DateTime.UtcNow.AddSeconds(45);
        bool failed = false;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var reply = await http.GetFromJsonAsync<GameWindow>(_endpoint, _cts.Token);
                if (reply?.Xid is { } text && ulong.TryParse(text, out ulong xid) && xid != 0)
                    await Dispatcher.UIThread.InvokeAsync(() => Embed(xid));
            }
            catch (Exception) when (!_cts.IsCancellationRequested)
            {
            }
            if (_game == 0 && !failed && DateTime.UtcNow > giveUp)
            {
                failed = true;
                Console.Error.WriteLine("[host] no game window to embed; Skua stays a bar above the game");
                Dispatcher.UIThread.Post(() => Failed?.Invoke());
            }
            await Task.Delay(TimeSpan.FromSeconds(2), _cts.Token).ContinueWith(_ => { });
        }
    }

    private sealed record GameWindow(string? Xid);

    private void Embed(ulong xid)
    {
        if (_display == IntPtr.Zero)
        {
            _display = X.XOpenDisplay(null);
            if (_display == IntPtr.Zero)
                return;
        }
        if (_window.TryGetPlatformHandle()?.Handle is not { } handle || handle == IntPtr.Zero)
            return;
        ulong host = (ulong)handle;
        if (xid == _game && host == _host && ParentOf(xid) == host)
        {
            // Still ours; put it back if something (main.js's own placement
            // right after start) moved it.
            _placed = default;
            Place();
            return;
        }

        // Take it from the window manager (which unmanages it on unmap), then
        // make it a child of ours.
        X.XUnmapWindow(_display, xid);
        X.XSync(_display, false);
        Thread.Sleep(150);
        X.XReparentWindow(_display, xid, host, 0, 0);
        AddToAvaloniaSaveSet(xid);
        X.XSelectInput(_display, xid, X.EnterWindowMask | X.LeaveWindowMask);
        X.XMapWindow(_display, xid);
        X.XSync(_display, false);
        bool first = _game == 0;
        _game = xid;
        _host = host;
        _placed = default;
        Place();
        Console.WriteLine($"[host] game window 0x{xid:x} embedded");
        if (first)
            Embedded?.Invoke();
    }

    // The save-set only protects windows inside windows created by the same
    // connection, so it has to be Avalonia's, not ours. Its X11 platform is
    // internal; its Display property is public.
    private static void AddToAvaloniaSaveSet(ulong window)
    {
        try
        {
            const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance;
            object? locator = typeof(AvaloniaLocator).GetProperty("Current", any)?.GetValue(null);
            object? platform = locator?.GetType().GetMethod("GetService", new[] { typeof(Type) })
                ?.Invoke(locator, new object[] { typeof(global::Avalonia.Platform.IWindowingPlatform) });
            if (platform?.GetType().GetProperty("Display", any)?.GetValue(platform) is IntPtr display && display != IntPtr.Zero)
            {
                X.XAddToSaveSet(display, window);
                X.XFlush(display);
                return;
            }
            Console.Error.WriteLine($"[host] could not reach Avalonia's X connection ({platform?.GetType().FullName ?? "no platform"}); the game window may close with Skua");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[host] save-set: {e.Message}");
        }
    }

    /// <summary>Gives the game window back to the desktop (on exit).</summary>
    public void Release()
    {
        lock (_releaseLock)
        {
            if (_display == IntPtr.Zero || _game == 0)
                return;
            ulong game = _game;
            _game = 0;
            ulong root = X.XDefaultRootWindow(_display);
            X.XUnmapWindow(_display, game);
            X.XReparentWindow(_display, game, root, 0, 0);
            X.XMapWindow(_display, game);
            X.XSync(_display, false);
            Console.WriteLine($"[host] game window 0x{game:x} handed back to the desktop");
        }
    }

    private ulong ParentOf(ulong window)
    {
        if (X.XQueryTree(_display, window, out _, out ulong parent, out IntPtr children, out _) == 0)
            return 0;
        if (children != IntPtr.Zero)
            X.XFree(children);
        return parent;
    }

    /// <summary>Keeps the game window over the game area.</summary>
    private void Place()
    {
        if (_game == 0 || _display == IntPtr.Zero)
            return;
        double scale = _window.RenderScaling;
        Point? origin = _area.TranslatePoint(new Point(0, 0), _window);
        if (origin is not { } o)
            return;
        var rect = ((int)(o.X * scale), (int)(o.Y * scale),
                    Math.Max(1, (int)(_area.Bounds.Width * scale)), Math.Max(1, (int)(_area.Bounds.Height * scale)));
        if (rect == _placed)
            return;
        _placed = rect;
        X.XMoveResizeWindow(_display, _game, rect.Item1, rect.Item2, (uint)rect.Item3, (uint)rect.Item4);
        X.XFlush(_display);
    }

    // Keyboard focus follows the pointer between the game and Skua: the
    // window manager only gives focus to Skua's window, which is the one it
    // manages, and the game needs the keys (skills, chat).
    private async Task FocusLoop()
    {
        IntPtr ev = Marshal.AllocHGlobal(256);
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                IntPtr display = _display;
                if (display != IntPtr.Zero && _game != 0)
                {
                    while (X.XPending(display) > 0)
                    {
                        X.XNextEvent(display, ev);
                        int type = Marshal.ReadInt32(ev);
                        if (type == X.EnterNotify && _game != 0)
                            X.XSetInputFocus(display, _game, X.RevertToParent, 0);
                        else if (type == X.LeaveNotify && _host != 0)
                            X.XSetInputFocus(display, _host, X.RevertToParent, 0);
                    }
                    X.XFlush(display);
                }
                await Task.Delay(25);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ev);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        Release();
        if (_display != IntPtr.Zero)
        {
            X.XCloseDisplay(_display);
            _display = IntPtr.Zero;
        }
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
        [DllImport(Lib)] public static extern int XCloseDisplay(IntPtr display);
        [DllImport(Lib)] public static extern int XReparentWindow(IntPtr display, ulong window, ulong parent, int x, int y);
        [DllImport(Lib)] public static extern int XAddToSaveSet(IntPtr display, ulong window);
        [DllImport(Lib)] public static extern int XMapWindow(IntPtr display, ulong window);
        [DllImport(Lib)] public static extern int XUnmapWindow(IntPtr display, ulong window);
        [DllImport(Lib)] public static extern int XMoveResizeWindow(IntPtr display, ulong window, int x, int y, uint width, uint height);
        [DllImport(Lib)] public static extern int XSelectInput(IntPtr display, ulong window, long mask);
        [DllImport(Lib)] public static extern int XSetInputFocus(IntPtr display, ulong window, int revertTo, ulong time);
        [DllImport(Lib)] public static extern int XSync(IntPtr display, bool discard);
        [DllImport(Lib)] public static extern int XFlush(IntPtr display);
        [DllImport(Lib)] public static extern int XPending(IntPtr display);
        [DllImport(Lib)] public static extern int XNextEvent(IntPtr display, IntPtr ev);
        [DllImport(Lib)] public static extern int XFree(IntPtr data);
        [DllImport(Lib)] public static extern ulong XDefaultRootWindow(IntPtr display);
        [DllImport(Lib)] public static extern int XQueryTree(IntPtr display, ulong window, out ulong root, out ulong parent, out IntPtr children, out uint count);
    }
}
