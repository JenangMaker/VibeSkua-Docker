using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Skua.App.Avalonia;

/// <summary>
/// The KasmVNC desktop's window manager (openbox, as the LinuxServer base
/// image configures it) maximizes every new window. In WPF the game was inside
/// Skua's main window; here it is the Electron window beside it, so Skua's
/// windows undo that: the main window becomes a bar across the top of the
/// screen (main.js puts the game below it), the others open at their own
/// size, centred.
/// </summary>
public static class WindowPlacement
{
    /// <summary>Height the main window bar takes; main.js leaves the same (SKUA_BAR_HEIGHT).</summary>
    public const int BarHeight = 80;

    public static void AsTopBar(Window window) => Apply(window, w =>
    {
        if ((w.Screens.ScreenFromWindow(w) ?? w.Screens.Primary) is not { } screen)
            return;
        var area = screen.WorkingArea;
        double scale = screen.Scaling;
        w.SizeToContent = SizeToContent.Height;
        w.Width = area.Width / scale;
        w.Position = new PixelPoint(area.X, area.Y);
    });

    public static void Centred(Window window) => Apply(window, w =>
    {
        if ((w.Screens.ScreenFromWindow(w) ?? w.Screens.Primary) is not { } screen)
            return;
        var area = screen.WorkingArea;
        double scale = screen.Scaling;
        int width = (int)(w.Bounds.Width * scale), height = (int)(w.Bounds.Height * scale);
        w.Position = new PixelPoint(area.X + Math.Max(0, (area.Width - width) / 2), area.Y + Math.Max(0, (area.Height - height) / 2));
    });

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
