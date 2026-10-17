using System.Runtime.InteropServices;

namespace Skua.App.Avalonia;

/// <summary>
/// Windows' side of embedding a window of another process in ours, as the X11
/// code in GameEmbed and TabHostWindow does on Linux, and as Skua.App.WPF's
/// tabbed host does with SetParent: the window becomes a child of ours (no
/// frame, no taskbar button), placed in our client area. Windows joins the
/// input of a parent and its child, so a click in the game gives it the
/// keyboard; there is no focus juggling to do here.
/// </summary>
internal static class Win32Embed
{
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const long WS_CHILD = 0x40000000L;
    private const long WS_POPUP = 0x80000000L;
    private const long WS_VISIBLE = 0x10000000L;
    private const long WS_CLIPSIBLINGS = 0x04000000L;
    private const long WS_OVERLAPPEDWINDOW = 0x00CF0000L;   // caption, system menu, sizing frame, min/max boxes
    private const long WS_EX_APPWINDOW = 0x00040000L;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004,
        SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020, SWP_SHOWWINDOW = 0x0040;
    private const uint GA_PARENT = 1;
    private const int SW_SHOW = 5;

    /// <summary>Makes <paramref name="window"/> a frameless child of <paramref name="parent"/>, at 0,0.</summary>
    public static void MakeChild(IntPtr window, IntPtr parent)
    {
        // A child style before the new parent, as SetParent's documentation asks.
        long style = (long)GetWindowLongPtr(window, GWL_STYLE);
        style = (style & ~(WS_POPUP | WS_OVERLAPPEDWINDOW)) | WS_CHILD | WS_CLIPSIBLINGS;
        SetWindowLongPtr(window, GWL_STYLE, (IntPtr)style);
        long ex = (long)GetWindowLongPtr(window, GWL_EXSTYLE);
        SetWindowLongPtr(window, GWL_EXSTYLE, (IntPtr)(ex & ~WS_EX_APPWINDOW));
        SetParent(window, parent);
        SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
    }

    public static IntPtr ParentOf(IntPtr window) => GetAncestor(window, GA_PARENT);

    /// <summary>
    /// Whether <paramref name="window"/> is still a child of <paramref name="parent"/>, style
    /// included: Ruffle shows its window once skua.swf has loaded, and winit then rewrites the
    /// window's style from its own flags. Embedded before that (a slow first start), the game
    /// kept its parent but lost WS_CHILD, so it sat outside VibeSkua's window.
    /// </summary>
    public static bool IsEmbeddedIn(IntPtr window, IntPtr parent) =>
        ParentOf(window) == parent && (GetWindowLongPtr(window, GWL_STYLE).ToInt64() & WS_CHILD) != 0;

    /// <summary>Places a child window in its parent's client area (pixels).</summary>
    public static void Place(IntPtr window, int x, int y, int width, int height) =>
        SetWindowPos(window, IntPtr.Zero, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE);

    /// <summary>Puts a child window above its siblings.</summary>
    public static void Raise(IntPtr window) =>
        SetWindowPos(window, IntPtr.Zero /* HWND_TOP */, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    public static bool IsShown(IntPtr window) => (GetWindowLongPtr(window, GWL_STYLE).ToInt64() & WS_VISIBLE) != 0;

    public static void Show(IntPtr window) => ShowWindow(window, SW_SHOW);

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
}
