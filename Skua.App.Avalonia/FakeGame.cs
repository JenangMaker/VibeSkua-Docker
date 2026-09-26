using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace Skua.App.Avalonia;

/// <summary>
/// --fake-game [port]: development stand-in for the vibeskua-web Electron
/// window, to test GameEmbed without the container: a plain window, and
/// /game-window on 127.0.0.1:port (8770) serving its X11 id as main.js does.
/// </summary>
public static class FakeGame
{
    public static void Run(int port)
    {
        // A bare Xlib window: no toolkit of its own reacting to being moved
        // between parents, like a plain client window.
        IntPtr display = XOpenDisplay(null);
        ulong root = XDefaultRootWindow(display);
        ulong xid = XCreateSimpleWindow(display, root, 0, 0, 958, 550, 0, 0, 0x2E7D32);
        XStoreName(display, xid, "Fake game");
        XMapWindow(display, xid);
        XFlush(display);
        Console.WriteLine($"[fake-game] window 0x{xid:x}");
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                var ctx = await listener.GetContextAsync();
                byte[] body = Encoding.UTF8.GetBytes($"{{\"xid\":\"{xid}\"}}");
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(body);
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
    [DllImport("libX11.so.6")] private static extern int XFlush(IntPtr display);
}
