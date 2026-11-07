using Avalonia.Controls;
using Avalonia.Interactivity;
using Skua.Core.ViewModels;

namespace Skua.App.Avalonia.Views;

public partial class ToPickupDropsUserControl : UserControl
{
    public ToPickupDropsUserControl()
    {
        InitializeComponent();
    }

    private void Remove_Click(object? sender, RoutedEventArgs e) =>
        Selection.Run((DataContext as ToPickupDropsViewModel)?.RemoveDropsCommand, List);
}
