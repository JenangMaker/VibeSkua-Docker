using Avalonia.Controls;
using Avalonia.Interactivity;
using Skua.Core.ViewModels;

namespace Skua.App.Avalonia.Views;

public partial class CBOClassEquipmentUserControl : UserControl
{
    public CBOClassEquipmentUserControl()
    {
        InitializeComponent();
    }

    private void Equip_DropDownOpened(object? sender, EventArgs e) =>
        (DataContext as CBOClassEquipmentViewModel)?.RefreshInventoryCommand.Execute(null);
}
