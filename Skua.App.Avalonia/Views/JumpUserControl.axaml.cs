using Avalonia.Controls;
using Avalonia.Interactivity;
using Skua.Core.ViewModels;

namespace Skua.App.Avalonia.Views;

public partial class JumpUserControl : UserControl
{
    public JumpUserControl()
    {
        InitializeComponent();
        // Refresh cells when the dropdown opens.
        Cells.DropDownOpened += (_, _) => (DataContext as JumpViewModel)?.UpdateCellsCommand.Execute(null);
    }
}
