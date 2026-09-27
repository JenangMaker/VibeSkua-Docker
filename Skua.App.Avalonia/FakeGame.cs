using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace Skua.App.Avalonia;

/// <summary>
/// --fake-game [port]: development stand-in for the vibeskua-web Electron
/// windows, to test GameEmbed and the tabs without the container: plain
/// windows, one per tab, and main.js's /game-window?instance=N and
/// /instances/N on 127.0.0.1:port (8770).
/// </summary>
public static class FakeGame
{
    private static readonly ulong[] Colours = [0x2E7D32, 0x1565C0, 0xC62828, 0x6A1B9A, 0xEF6C00, 0x00838F];

    public static void Run(int port)
    {
        // Bare Xlib windows: no toolkit of their own reacting to being moved
        // between parents, like a plain client window.
        IntPtr display = XOpenDisplay(null);
        ulong root = XDefaultRootWindow(display);
        var windows = new Dictionary<int, ulong>();
        var gate = new object();

        ulong Open(int n)
        {
            lock (gate)
            {
                if (windows.TryGetValue(n, out ulong existing))
                    return existing;
                ulong xid = XCreateSimpleWindow(display, root, 40 * n, 40 * n, 958, 550, 0, 0, Colours[n % Colours.Length]);
                XStoreName(display, xid, $"Fake game {n}");
                XMapWindow(display, xid);
                XFlush(display);
                windows[n] = xid;
                Console.WriteLine($"[fake-game] window {n}: 0x{xid:x}");
                return xid;
            }
        }

        void Close(int n)
        {
            lock (gate)
            {
                if (!windows.Remove(n, out ulong xid))
                    return;
                XDestroyWindow(display, xid);
                XFlush(display);
                Console.WriteLine($"[fake-game] window {n} closed");
            }
        }

        Open(0);
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                var ctx = await listener.GetContextAsync();
                string path = ctx.Request.Url!.AbsolutePath;
                string body = "{}";
                if (path == "/game-window")
                {
                    int n = int.TryParse(ctx.Request.QueryString["instance"], out int i) ? i : 0;
                    lock (gate)
                        body = windows.TryGetValue(n, out ulong xid) ? $"{{\"xid\":\"{xid}\"}}" : "{\"xid\":null}";
                }
                else if (path.StartsWith("/instances/") && int.TryParse(path["/instances/".Length..], out int n))
                {
                    if (ctx.Request.HttpMethod == "DELETE")
                        Close(n);
                    else
                        Open(n);
                }
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });
        Thread.Sleep(Timeout.Infinite);
    }

    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(string? name);
    [DllImport("libX11.so.6")] private static extern ulong XDefaultRootWindow(IntPtr display);
    [DllImport("libX11.so.6")] private static extern ulong XCreateSimpleWindow(IntPtr display, ulong parent, int x, int y, uint w, uint h, uint border, ulong borderColor, ulong background);
    [DllImport("libX11.so.6")] private static extern int XStoreName(IntPtr display, ulong window, string name);
    [DllImport("libX11.so.6")] private static extern int XMapWindow(IntPtr display, ulong window);
    [DllImport("libX11.so.6")] private static extern int XDestroyWindow(IntPtr display, ulong window);
    [DllImport("libX11.so.6")] private static extern int XFlush(IntPtr display);
}
