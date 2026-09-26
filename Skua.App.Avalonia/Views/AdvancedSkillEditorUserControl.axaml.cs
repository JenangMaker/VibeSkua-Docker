using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Skua.Core.ViewModels;

namespace Skua.App.Avalonia.Views;

public partial class AdvancedSkillEditorUserControl : UserControl
{
    public AdvancedSkillEditorUserControl()
    {
        InitializeComponent();
        SkillsList.KeyDown += SkillsList_KeyDown;
    }

    // The WPF list's key bindings.
    private void SkillsList_KeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not AdvancedSkillEditorViewModel vm)
            return;
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        System.Windows.Input.ICommand? command = e.Key switch
        {
            Key.Up => ctrl ? vm.MoveSkillUpCommand : vm.SelectSkillUpCommand,
            Key.Down => ctrl ? vm.MoveSkillDownCommand : vm.SelectSkillDownCommand,
            Key.Delete => e.KeyModifiers.HasFlag(KeyModifiers.Alt) ? vm.ClearSkillsCommand : vm.RemoveSkillCommand,
            Key.Enter => vm.EditSkillCommand,
            _ => null,
        };
        if (command is null)
            return;
        if (command.CanExecute(null))
            command.Execute(null);
        e.Handled = true;
    }
}
