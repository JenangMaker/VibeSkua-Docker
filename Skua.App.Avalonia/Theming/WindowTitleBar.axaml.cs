using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Material.Icons;

namespace Skua.App.Avalonia.Theming;

/// <summary>
/// The 2.x title bar: round app badge, title, minimize/maximize/close. From upstream VibeSkua 2.1.2
/// (Skua.Shared.Avalonia/Controls/Shell/WindowTitleBar), without its artwork options.
/// <para>
/// Ours: the window it is put in gets its frameless setup here (before it is shown), so a window only
/// needs <c>&lt;theming:WindowTitleBar DockPanel.Dock="Top" /&gt;</c>. The title follows the window's
/// Title unless <see cref="TitleText"/> is set. <see cref="Extra"/> holds a window's own controls
/// between the title and the buttons. SKUA_TITLEBAR=system keeps the system title bar instead (the
/// bar hides itself), for window managers that handle frameless windows badly.
/// </para>
/// </summary>
public partial class WindowTitleBar : UserControl
{
    public static readonly bool UseSystem =
        string.Equals(Environment.GetEnvironmentVariable("SKUA_TITLEBAR"), "system", StringComparison.OrdinalIgnoreCase);

    public static readonly StyledProperty<string?> TitleTextProperty =
        AvaloniaProperty.Register<WindowTitleBar, string?>(nameof(TitleText));

    public static readonly StyledProperty<object?> ExtraProperty =
        AvaloniaProperty.Register<WindowTitleBar, object?>(nameof(Extra));

    private static readonly Uri IconUri = new("avares://Skua.App.Avalonia/Assets/SkuaShellImage.png");
    private static IImage? _icon;
    private Window? _window;
    private bool _titleFromWindow;

    public string? TitleText
    {
        get => GetValue(TitleTextProperty);
        set => SetValue(TitleTextProperty, value);
    }

    public object? Extra
    {
        get => GetValue(ExtraProperty);
        set => SetValue(ExtraProperty, value);
    }

    public WindowTitleBar()
    {
        InitializeComponent();
        IsVisible = !UseSystem;
        IconImage.Source = _icon ??= LoadIcon();
        PointerPressed += OnTitleBarPointerPressed;
        MinimizeButton.Click += (_, _) => { if (_window is not null) _window.WindowState = WindowState.Minimized; };
        MaximizeButton.Click += (_, _) => ToggleMaximized();
        CloseButton.Click += (_, _) => _window?.Close();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (UseSystem || this.GetVisualRoot() is not Window window || ReferenceEquals(window, _window))
            return;
        _window = window;

        // Frameless with the client area over the frame, as upstream: the system keeps the border
        // (resizing, snapping, the shadow); this bar is the caption. Set while the window is being
        // built, before it is shown.
        window.SystemDecorations = SystemDecorations.BorderOnly;
        window.ExtendClientAreaToDecorationsHint = true;
        window.ExtendClientAreaChromeHints = global::Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome;
        window.ExtendClientAreaTitleBarHeightHint = 32;

        _titleFromWindow = TitleText is null;
        if (_titleFromWindow)
            TitleText = window.Title;
        MaximizeButton.IsVisible = window.CanResize;
        UpdateMaximizeGlyph(window.WindowState);
        window.PropertyChanged += OnWindowPropertyChanged;
        if (window.CanResize)
            MaximizedResizeGuard.Attach(window);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_window is null)
            return;
        _window.PropertyChanged -= OnWindowPropertyChanged;
        MaximizedResizeGuard.Detach(_window);
        _window = null;
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty && e.NewValue is WindowState state)
            UpdateMaximizeGlyph(state);
        else if (e.Property == Window.TitleProperty && _titleFromWindow)
            TitleText = e.NewValue as string;
        else if (e.Property == Window.CanResizeProperty && e.NewValue is bool canResize)
            MaximizeButton.IsVisible = canResize;
    }

    private void UpdateMaximizeGlyph(WindowState state)
    {
        bool maximized = state == WindowState.Maximized;
        MaximizeIcon.Kind = maximized ? MaterialIconKind.WindowRestore : MaterialIconKind.WindowMaximize;
        ToolTip.SetTip(MaximizeButton, maximized ? "Restore" : "Maximize");
    }

    private void ToggleMaximized()
    {
        if (_window is null || !_window.CanResize)
            return;
        _window.WindowState = _window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_window is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        // Presses on the window's own controls (Extra) are theirs.
        if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;
        if (e.ClickCount == 2)
        {
            ToggleMaximized();
            e.Handled = true;
            return;
        }
        _window.BeginMoveDrag(e);
    }

    private static IImage? LoadIcon()
    {
        try
        {
            // The source is 2000x2000 but drawn at 30 DIP: decode small, as upstream does, or it aliases.
            using var stream = AssetLoader.Open(IconUri);
            return Bitmap.DecodeToWidth(stream, 128, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
