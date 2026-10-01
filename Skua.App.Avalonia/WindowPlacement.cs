using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Skua.App.Avalonia;

/// <summary>
/// The KasmVNC desktop's window manager (openbox, as the LinuxServer base
/// image configures it) maximizes every new window. In WPF the game was inside
/// Skua's main window; here it is the Electron window beside it, so Skua's
/// windows undo that except the main window, which holds the game
/// (GameEmbed): the others open at their own size, centred.
/// </summary>
public static class WindowPlacement
{
    /// <summary>Height the main window bar takes; main.js leaves the same (SKUA_BAR_HEIGHT).</summary>
    public const int BarHeight = 80;

    /// <summary>
    /// The main window's fallback when the game cannot be embedded: a bar
    /// across the top of the screen, the game window below it (main.js).
    /// </summary>
    public static void ToTopBar(Window w, Control gameArea)
    {
        gameArea.IsVisible = false;
        if (w.WindowState == WindowState.Maximized)
            w.WindowState = WindowState.Normal;
        if ((w.Screens.ScreenFromWindow(w) ?? w.Screens.Primary) is not { } screen)
            return;
        var area = screen.WorkingArea;
        w.SizeToContent = SizeToContent.Height;
        w.Width = area.Width / screen.Scaling;
        w.Position = new PixelPoint(area.X, area.Y);
    }

    public static void Centred(Window window) => Apply(window, w =>
    {
        if ((w.Screens.ScreenFromWindow(w) ?? w.Screens.Primary) is not { } screen)
            return;
        var area = screen.WorkingArea;
        double scale = screen.Scaling;
        int width = (int)(w.Bounds.Width * scale), height = (int)(w.Bounds.Height * scale);
        w.Position = new PixelPoint(area.X + Math.Max(0, (area.Width - width) / 2), area.Y + Math.Max(0, (area.Height - height) / 2));
    });

    /// <summary>
    /// Keeps a window that fills the desktop filling it when the desktop's size
    /// changes. Selkies resizes the X screen to each browser that connects, and
    /// openbox maximizes a window only when it maps, so the window would keep
    /// the previous browser's size. Re-maximizing (via Normal, so the window
    /// manager recomputes) follows the new size.
    /// </summary>
    public static void FillScreenOnChange(Window window)
    {
        IDisposable? pending = null;
        window.Opened += (_, _) => window.Screens.Changed += (_, _) =>
        {
            // A resize arrives as a burst of changes: act on the last one.
            pending?.Dispose();
            pending = DispatcherTimer.RunOnce(() =>
            {
                window.WindowState = WindowState.Normal;
                window.WindowState = WindowState.Maximized;
            }, TimeSpan.FromMilliseconds(400));
        };
    }

    // The window manager maximizes at map time, which can land after Opened;
    // undo any maximize in the first few seconds, then place the window.
    private static void Apply(Window window, Action<Window> place)
    {
        var until = DateTime.UtcNow.AddSeconds(3);
        void Restore()
        {
            if (window.WindowState == WindowState.Maximized)
                window.WindowState = WindowState.Normal;
            place(window);
        }
        window.Opened += (_, _) =>
        {
            Restore();
            DispatcherTimer.RunOnce(Restore, TimeSpan.FromMilliseconds(300));
        };
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty && window.WindowState == WindowState.Maximized && DateTime.UtcNow < until)
                Dispatcher.UIThread.Post(Restore);
        };
    }
}
