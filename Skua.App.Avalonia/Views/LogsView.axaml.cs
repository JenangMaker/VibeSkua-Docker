using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Skua.App.Avalonia.Views;

public partial class LogsView : UserControl
{
    public LogsView()
    {
        InitializeComponent();
    }

    // The header's Copy / Deselect act on the log shown below.
    private void Copy_Click(object? sender, RoutedEventArgs e) => LogTabUserControl.In(this)?.Copy();
    private void Deselect_Click(object? sender, RoutedEventArgs e) => LogTabUserControl.In(this)?.Deselect();
}
