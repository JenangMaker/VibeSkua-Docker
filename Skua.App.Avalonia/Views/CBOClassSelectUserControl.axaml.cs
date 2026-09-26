using Avalonia.Controls;
using Avalonia.Interactivity;
using Skua.Core.ViewModels;

namespace Skua.App.Avalonia.Views;

public partial class CBOClassSelectUserControl : UserControl
{
    public CBOClassSelectUserControl()
    {
        InitializeComponent();
    }

    // Reload the class list when a class box opens, keeping the selections.
    private void ClassBox_DropDownOpened(object? sender, EventArgs e)
    {
        if (DataContext is not CBOClassSelectViewModel vm)
            return;
        string? solo = vm.SelectedSoloClass, farm = vm.SelectedFarmClass, dodge = vm.SelectedDodgeClass, boss = vm.SelectedBossClass;
        vm.ReloadClassesCommand.Execute(null);
        vm.SelectedSoloClass = solo;
        vm.SelectedFarmClass = farm;
        vm.SelectedDodgeClass = dodge;
        vm.SelectedBossClass = boss;
    }
}
