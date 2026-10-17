using Avalonia.Controls;
using Avalonia.Interactivity;
using Skua.Core.ViewModels.Manager;

namespace Skua.App.Avalonia.Views;

public partial class SelectGroupDialog : UserControl
{
    public SelectGroupDialog()
    {
        InitializeComponent();
    }

    private void Confirm_Click(object? sender, RoutedEventArgs e)
    {
        bool chosen = DataContext is SelectGroupDialogViewModel { SelectedGroup: not null };
        if (DataContext is SelectGroupDialogViewModel vm)
            vm.ConfirmCommand.Execute(null);
        HostDialog.Close(this, chosen);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => HostDialog.Close(this, false);
}
