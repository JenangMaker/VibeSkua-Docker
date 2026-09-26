using Avalonia.Controls;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Interfaces;

namespace Skua.App.Avalonia;

/// <summary>
/// A window around one view model (Skua.WPF's HostWindow): the view comes
/// from the view model's DataTemplate in App.axaml.
/// </summary>
public partial class HostWindow : Window
{
    public HostWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ApplySize();
    }

    /// <summary>Closing only hides the window (managed windows keep their state).</summary>
    public bool HideOnClose { get; init; }

    // The WPF style bound Width/Height to the view model, sizing to content
    // where it gave 0, and made the window fixed-size unless CanResize.
    private void ApplySize()
    {
        if (DataContext is not IManagedWindow vm)
        {
            SizeToContent = SizeToContent.WidthAndHeight;
            return;
        }
        if (vm.Width > 0) Width = vm.Width;
        if (vm.Height > 0) Height = vm.Height;
        SizeToContent = (vm.Width, vm.Height) switch
        {
            (0, 0) => SizeToContent.WidthAndHeight,
            (0, _) => SizeToContent.Width,
            (_, 0) => SizeToContent.Height,
            _ => SizeToContent.Manual,
        };
        CanResize = vm.CanResize;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (HideOnClose && !e.IsProgrammatic)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (DataContext is { } vm)
        {
            StrongReferenceMessenger.Default.UnregisterAll(vm);
            (vm as IDisposable)?.Dispose();
        }
        DataContext = null;
    }
}
