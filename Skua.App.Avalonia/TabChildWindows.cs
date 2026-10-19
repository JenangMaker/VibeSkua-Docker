using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Skua.Linux;

namespace Skua.App.Avalonia;

/// <summary>
/// Linux: a tab's own windows (Script Loader, Options, dialogs) as transients
/// of the tab host's window. Each tab is a process of its own, so a window it
/// opened from a click in the tab host (another process) is, to GNOME's focus
/// stealing prevention, a background program's: on Fedora's Wayland session
/// (XWayland) it opened behind the VibeSkua window and seemed not to open at
/// all. A transient of the focused window is put above it. The tab host passes
/// its window as SKUA_TAB_HOST_XID (TabHostWindow.StartProcess).
/// </summary>
internal static class TabChildWindows
{
    private static readonly ulong HostXid =
        ulong.TryParse(SkuaRuntime.EnvRaw("SKUA_TAB_HOST_XID"), out ulong xid) ? xid : 0;
    private static readonly object Lock = new();
    private static IntPtr _display;

    /// <summary>Call before the window is first shown: a window manager reads it as the window maps.</summary>
    public static void Attach(Window window)
    {
        if (!OperatingSystem.IsLinux() || App.Mode != AppMode.TabChild || HostXid == 0)
            return;
        try
        {
            if (window.TryGetPlatformHandle()?.Handle is not { } handle || handle == IntPtr.Zero)
                return;
            lock (Lock)
            {
                // A connection of our own: the property lives on the X server,
                // whichever client sets it.
                if (_display == IntPtr.Zero)
                    _display = XOpenDisplay(null);
                if (_display == IntPtr.Zero)
                    return;
                XSetTransientForHint(_display, (ulong)(long)handle, HostXid);
                XFlush(_display);
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[host] could not keep a window above VibeSkua's: {e.Message}");
        }
    }

    /// <summary>
    /// The middle of the tab host's window, in screen pixels: a tab's windows
    /// open centred on that window's monitor. Avalonia places a window not yet
    /// shown on the primary one, which with two monitors need not be the one
    /// VibeSkua is on (and was a screen the VM's console did not show).
    /// </summary>
    public static PixelPoint? HostCentre()
    {
        if (!OperatingSystem.IsLinux() || App.Mode != AppMode.TabChild || HostXid == 0)
            return null;
        try
        {
            lock (Lock)
            {
                if (_display == IntPtr.Zero)
                    _display = XOpenDisplay(null);
                if (_display == IntPtr.Zero
                    || XGetGeometry(_display, HostXid, out ulong root, out _, out _, out uint width, out uint height, out _, out _) == 0
                    || !XTranslateCoordinates(_display, HostXid, root, 0, 0, out int x, out int y, out _))
                    return null;
                return new PixelPoint(x + (int)width / 2, y + (int)height / 2);
            }
        }
        catch
        {
            return null;
        }
    }

    private const string Lib = "libX11.so.6";
    [DllImport(Lib)] private static extern IntPtr XOpenDisplay(string? name);
    [DllImport(Lib)] private static extern int XSetTransientForHint(IntPtr display, ulong window, ulong parent);
    [DllImport(Lib)] private static extern int XFlush(IntPtr display);
    [DllImport(Lib)] private static extern int XGetGeometry(IntPtr display, ulong drawable, out ulong root, out int x, out int y, out uint width, out uint height, out uint border, out uint depth);
    [DllImport(Lib)] private static extern bool XTranslateCoordinates(IntPtr display, ulong src, ulong dest, int srcX, int srcY, out int destX, out int destY, out ulong child);
}
