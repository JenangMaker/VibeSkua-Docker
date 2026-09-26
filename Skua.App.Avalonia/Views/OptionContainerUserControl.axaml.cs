using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Skua.Core.ViewModels;

namespace Skua.App.Avalonia.Views;

public partial class OptionContainerUserControl : UserControl
{
    public OptionContainerUserControl()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            // WPF grouped the rows by Category (a CollectionViewSource); the
            // uncategorised group is "Options" and starts expanded.
            Groups.ItemsSource = (DataContext as OptionContainerViewModel)?.Options
                .GroupBy(o => o.Category ?? "")
                .Select(g => new OptionGroup(string.IsNullOrEmpty(g.Key) ? "Options" : g.Key, string.IsNullOrEmpty(g.Key), g.ToList()))
                .ToList();
        };
    }

    public sealed record OptionGroup(string Name, bool IsExpanded, List<OptionContainerItemViewModel> Items);

    // Clicking anywhere on a row selects it, for the description below.
    private void Row_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: OptionContainerItemViewModel item } && DataContext is OptionContainerViewModel vm)
            vm.SelectedOption = item;
    }
}
