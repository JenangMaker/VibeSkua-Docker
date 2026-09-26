using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;
using Skua.Core.ViewModels;
using Skua.Core.Models.Items;

namespace Skua.App.Avalonia.Views;

public partial class CurrentDropsUserControl : UserControl
{
    public CurrentDropsUserControl()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        // Debounced search (300 ms); Enter searches at once.
        SearchBox.TextChanged += (_, _) =>
        {
            _debounce?.Dispose();
            _debounce = DispatcherTimer.RunOnce(() => _drops?.Refresh(), TimeSpan.FromMilliseconds(300));
        };
        SearchBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            _debounce?.Dispose();
            _drops?.Refresh();
            e.Handled = true;
        };
    }

    private UiMirror<ItemBase>? _drops;
    private IDisposable? _debounce;

    private void Attach()
    {
        _drops?.Dispose();
        if (DataContext is not CurrentDropsViewModel vm)
            return;
        _drops = new UiMirror<ItemBase>(vm.CurrentDrops)
        {
            Filter = i => string.IsNullOrEmpty(SearchBox.Text) || i.ToString()!.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase),
        };
        CurrentDropsListBox.ItemsSource = _drops.Items;
    }

    private void PickupSelected_Click(object? sender, RoutedEventArgs e) =>
        Selection.Run((DataContext as CurrentDropsViewModel)?.PickupSelectedCommand, CurrentDropsListBox);
}
