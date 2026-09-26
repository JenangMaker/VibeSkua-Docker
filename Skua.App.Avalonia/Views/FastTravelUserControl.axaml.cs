using Avalonia.Controls;
using Avalonia.Interactivity;
using Skua.Core.ViewModels;

namespace Skua.App.Avalonia.Views;

public partial class FastTravelUserControl : UserControl
{
    public FastTravelUserControl()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        SearchBox.TextChanged += (_, _) => _items?.Refresh();
    }

    private UiMirror<FastTravelItemViewModel>? _items;

    private void Attach()
    {
        _items?.Dispose();
        if (DataContext is not FastTravelViewModel vm)
            return;
        _items = new UiMirror<FastTravelItemViewModel>(vm.FastTravelItems)
        {
            Filter = i => string.IsNullOrEmpty(SearchBox.Text) || i.DescriptionName.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase),
        };
        FastTravelControl.ItemsSource = _items.Items;
    }
}
