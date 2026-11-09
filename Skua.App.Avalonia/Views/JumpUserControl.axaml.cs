using Avalonia.Controls;
using Avalonia.Input;
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
        // Tunnel, so it runs before the ComboBox's own key handling (upstream dbc4fe8, 51dac37).
        AddHandler(InputElement.KeyDownEvent, Cells_KeyDown, RoutingStrategies.Tunnel);
    }

    // Up / Down on the closed cell list steps to the previous / next cell.
    private void Cells_KeyDown(object? sender, KeyEventArgs e)
    {
        if (!Cells.IsFocused || Cells.IsDropDownOpen || Cells.ItemCount == 0)
            return;

        int direction = e.Key switch
        {
            Key.Up => -1,
            Key.Down => 1,
            _ => 0
        };
        if (direction == 0)
            return;

        int selectedIndex = Cells.SelectedIndex;
        if (selectedIndex < 0)
            selectedIndex = direction > 0 ? -1 : Cells.ItemCount;

        int nextIndex = System.Math.Clamp(selectedIndex + direction, 0, Cells.ItemCount - 1);
        if (nextIndex != Cells.SelectedIndex)
            Cells.SelectedIndex = nextIndex;

        e.Handled = true;
    }
}
