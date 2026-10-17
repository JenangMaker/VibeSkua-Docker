using Avalonia.Controls;
using Skua.App.Avalonia.Manager;

namespace Skua.App.Avalonia.Views;

public partial class RunningTabsView : UserControl
{
    public RunningTabsView()
    {
        InitializeComponent();
        // Polls VibeSkua only while this tab is on screen.
        AttachedToVisualTree += (_, _) => (DataContext as RunningTabsViewModel)?.SetVisible(true);
        DetachedFromVisualTree += (_, _) => (DataContext as RunningTabsViewModel)?.SetVisible(false);
    }
}
