using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Skua.App.Avalonia.Views;

public partial class ScriptLoaderView : UserControl
{
    public ScriptLoaderView()
    {
        InitializeComponent();
    }

    // The header's Copy / Deselect act on the log shown below.
    private void Copy_Click(object? sender, RoutedEventArgs e) => LogTabUserControl.In(this)?.Copy();
    private void Deselect_Click(object? sender, RoutedEventArgs e) => LogTabUserControl.In(this)?.Deselect();
}
