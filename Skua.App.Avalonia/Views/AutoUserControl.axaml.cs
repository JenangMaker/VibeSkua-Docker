using Avalonia.Controls;
using Avalonia.Interactivity;
using Skua.Core.ViewModels;

namespace Skua.App.Avalonia.Views;

public partial class AutoUserControl : UserControl
{
    public AutoUserControl()
    {
        InitializeComponent();
        ClassBox.DropDownOpened += (_, _) => (DataContext as AutoViewModel)?.ReloadClassesCommand.Execute(null);
    }
}
