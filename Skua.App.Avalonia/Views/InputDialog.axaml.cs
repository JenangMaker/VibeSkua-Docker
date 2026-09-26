using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Skua.App.Avalonia.Views;

public partial class InputDialog : UserControl
{
    public InputDialog()
    {
        InitializeComponent();
    }

    private void Confirm_Click(object? sender, RoutedEventArgs e) => HostDialog.Close(this, true);
    private void Cancel_Click(object? sender, RoutedEventArgs e) => HostDialog.Close(this, false);
}
