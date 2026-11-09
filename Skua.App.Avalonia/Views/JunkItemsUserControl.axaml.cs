using Avalonia.Controls;
using Avalonia.Interactivity;
using Skua.Core.ViewModels;

namespace Skua.App.Avalonia.Views;

public partial class JunkItemsUserControl : UserControl
{
    public JunkItemsUserControl()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        SearchBox.TextChanged += (_, _) => _items?.Refresh();
        CategoryFilter.SelectionChanged += (_, _) => _items?.Refresh();
        AcFilter.SelectionChanged += (_, _) => _items?.Refresh();
    }

    private UiMirror<JunkItemEntry>? _items;

    private void Attach()
    {
        _items?.Dispose();
        if (DataContext is not JunkItemsViewModel vm)
            return;
        _items = new UiMirror<JunkItemEntry>(vm.Items) { Filter = Filter };
        ItemsList.ItemsSource = _items.Items;
    }

    // By ID, name or category, then the category and AC filters.
    private bool Filter(JunkItemEntry entry)
    {
        if (CategoryFilter.SelectedItem is string category && category != JunkItemsViewModel.AllCategories
            && !string.Equals(entry.Category, category, StringComparison.OrdinalIgnoreCase))
            return false;
        if ((AcFilter.SelectedIndex == 1 && !entry.IsAc) || (AcFilter.SelectedIndex == 2 && entry.IsAc))
            return false;
        string search = SearchBox.Text?.Trim() ?? "";
        if (search.Length == 0)
            return true;
        return entry.ID.ToString().Contains(search, StringComparison.OrdinalIgnoreCase)
            || entry.Name?.Contains(search, StringComparison.OrdinalIgnoreCase) == true
            || entry.Category?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;
    }
}
