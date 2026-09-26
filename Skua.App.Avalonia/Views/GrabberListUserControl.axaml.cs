using Avalonia.Controls;
using Avalonia.Interactivity;
using Skua.Core.ViewModels;

namespace Skua.App.Avalonia.Views;

public partial class GrabberListUserControl : UserControl
{
    public GrabberListUserControl()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        SearchBox.TextChanged += (_, _) => _items?.Refresh();
    }

    private UiMirror<object>? _items;

    private void Attach()
    {
        _items?.Dispose();
        if (DataContext is not GrabberListViewModel vm)
            return;
        _items = new UiMirror<object>(vm.GrabbedItems)
        {
            Filter = i => string.IsNullOrWhiteSpace(SearchBox.Text) || (i.ToString()?.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase) ?? false),
        };
        GrabberListBox.ItemsSource = _items.Items;
    }

    private void UnselectAll_Click(object? sender, RoutedEventArgs e) => GrabberListBox.UnselectAll();

    // The WPF buttons passed the list's SelectedItems to the task.
    private void Task_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is GrabberTaskViewModel task)
            Selection.Run(task.GrabberTaskCommand, GrabberListBox);
    }
}
