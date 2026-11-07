using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Skua.App.Avalonia.Views;

public partial class ConsoleView : UserControl
{
    public ConsoleView()
    {
        InitializeComponent();
    }

    private void Clear_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        Snippet.Text = string.Empty;
        Snippet.Focus();
    }
}
