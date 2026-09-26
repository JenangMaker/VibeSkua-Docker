using Avalonia.Controls;
using Avalonia.Interactivity;
using Skua.Core.ViewModels;

namespace Skua.App.Avalonia.Views;

public partial class PacketLoggerView : UserControl
{
    public PacketLoggerView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        LogSearchBox.TextChanged += (_, _) => _logs?.Refresh();
        FilterSearchBox.TextChanged += (_, _) => _filters?.Refresh();
    }

    private UiMirror<string>? _logs;
    private UiMirror<PacketLogFilterViewModel>? _filters;

    private void Attach()
    {
        _logs?.Dispose();
        _filters?.Dispose();
        if (DataContext is not PacketLoggerViewModel vm)
            return;
        _logs = new UiMirror<string>(vm.PacketLogs)
        {
            Filter = p => string.IsNullOrEmpty(LogSearchBox.Text) || p.Contains(LogSearchBox.Text, StringComparison.OrdinalIgnoreCase),
        };
        _filters = new UiMirror<PacketLogFilterViewModel>(vm.PacketFilters)
        {
            Filter = f => string.IsNullOrEmpty(FilterSearchBox.Text) || f.Content.Contains(FilterSearchBox.Text, StringComparison.OrdinalIgnoreCase),
        };
        PacketsList.ItemsSource = _logs.Items;
        FiltersList.ItemsSource = _filters.Items;
    }

    private void UnselectAllLogs_Click(object? sender, RoutedEventArgs e) => PacketsList.UnselectAll();
}
