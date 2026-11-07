using Avalonia.Controls;
using Avalonia.Interactivity;
using Skua.Core.ViewModels;

namespace Skua.App.Avalonia.Views;

public partial class RegisteredQuestsUserControl : UserControl
{
    public RegisteredQuestsUserControl()
    {
        InitializeComponent();
    }

    private void Remove_Click(object? sender, RoutedEventArgs e) =>
        Selection.Run((DataContext as RegisteredQuestsViewModel)?.RemoveQuestsCommand, List);
}
