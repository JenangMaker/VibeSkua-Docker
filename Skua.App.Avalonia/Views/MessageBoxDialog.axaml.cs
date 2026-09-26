using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Skua.App.Avalonia.Views;

public partial class MessageBoxDialog : UserControl
{
    public MessageBoxDialog()
    {
        InitializeComponent();
    }

    // WPF: OK and No were IsCancel (DialogResult false); Yes set it true.
    private void Yes_Click(object? sender, RoutedEventArgs e) => HostDialog.Close(this, true);
    private void No_Click(object? sender, RoutedEventArgs e) => HostDialog.Close(this, false);
}
