using System.Runtime.InteropServices;
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

    private const string Lib = "libX11.so.6";
    [DllImport(Lib)] private static extern IntPtr XOpenDisplay(string? name);
    [DllImport(Lib)] private static extern int XSetTransientForHint(IntPtr display, ulong window, ulong parent);
    [DllImport(Lib)] private static extern int XFlush(IntPtr display);
}
