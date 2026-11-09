using Avalonia.Controls;
using Avalonia.Interactivity;
using System.ComponentModel;
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
        DetachedFromVisualTree += (_, _) => Unhook();
    }

    private PacketLoggerViewModel? _vm;

    private UiMirror<string>? _logs;
    private UiMirror<PacketLogFilterViewModel>? _filters;

    private void Attach()
    {
        _logs?.Dispose();
        _filters?.Dispose();
        Unhook();
        if (DataContext is not PacketLoggerViewModel vm)
            return;
        _vm = vm;
        foreach (PacketLogFilterViewModel filter in vm.PacketFilters)
            filter.PropertyChanged += Filter_PropertyChanged;
        UpdateShowCount();
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

    private void Unhook()
    {
        if (_vm is not null)
            foreach (PacketLogFilterViewModel filter in _vm.PacketFilters)
                filter.PropertyChanged -= Filter_PropertyChanged;
        _vm = null;
    }

    private void Filter_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PacketLogFilterViewModel.IsChecked))
            UpdateShowCount();
    }

    // "Show 12 / 12": ticked packet types out of all of them.
    private void UpdateShowCount()
    {
        if (_vm is not null)
            ShowCount.Text = $"{_vm.PacketFilters.Count(f => f.IsChecked)} / {_vm.PacketFilters.Count}";
    }

    private void ToggleLogging_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PacketLoggerViewModel vm)
            vm.IsReceivingPackets = !vm.IsReceivingPackets;
    }

    private void Copy_Click(object? sender, RoutedEventArgs e) => Behave.CopySelected(PacketsList);
}
