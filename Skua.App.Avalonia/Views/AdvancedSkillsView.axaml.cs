using System.ComponentModel;
using Avalonia.Controls;
using Skua.Core.ViewModels;

namespace Skua.App.Avalonia.Views;

public partial class AdvancedSkillsView : UserControl
{
    public AdvancedSkillsView()
    {
        InitializeComponent();
        Tabs.SelectionChanged += (_, _) => ShowTab(Tabs.SelectedIndex);
        DataContextChanged += (_, _) => Attach();
    }

    private AdvancedSkillsViewModel? _vm;

    private void ShowTab(int index)
    {
        EditPage.IsVisible = index == 0;
        SavedPage.IsVisible = index == 1;
        if (_vm is not null)
            _vm.EditExpanded = index == 0;
    }

    // The view model sets EditExpanded when a saved skill is sent to the editor.
    private void Attach()
    {
        if (_vm is not null)
            _vm.PropertyChanged -= Vm_PropertyChanged;
        _vm = DataContext as AdvancedSkillsViewModel;
        if (_vm is not null)
            _vm.PropertyChanged += Vm_PropertyChanged;
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AdvancedSkillsViewModel.EditExpanded) && _vm?.EditExpanded == true)
            Tabs.SelectedIndex = 0;
    }
}
