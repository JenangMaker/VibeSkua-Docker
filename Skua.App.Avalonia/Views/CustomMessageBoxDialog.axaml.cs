using Avalonia.Controls;
using Avalonia.Interactivity;
using Skua.Core.ViewModels;

namespace Skua.App.Avalonia.Views;

public partial class CustomMessageBoxDialog : UserControl
{
    public CustomMessageBoxDialog()
    {
        InitializeComponent();
    }

    private void Button_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || DataContext is not CustomDialogViewModel vm)
            return;
        string text = button.Content?.ToString() ?? string.Empty;
        vm.Result = new(text, vm.Buttons.IndexOf(text));
        HostDialog.Close(this, true);
    }
}
