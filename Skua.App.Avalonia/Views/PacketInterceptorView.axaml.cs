using Avalonia.Controls;
using Avalonia.Interactivity;
using System.ComponentModel;
using Skua.Core.ViewModels;

namespace Skua.App.Avalonia.Views;

public partial class PacketInterceptorView : UserControl
{
    public PacketInterceptorView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        SearchBox.TextChanged += (_, _) => _packets?.Refresh();
        FilterSearchBox.TextChanged += (_, _) => _filters?.Refresh();
        DetachedFromVisualTree += (_, _) => Detach();
    }

    private PacketInterceptorViewModel? _vm;
    private UiMirror<InterceptedPacketViewModel>? _packets;
    private UiMirror<PacketLogFilterViewModel>? _filters;

    private void Attach()
    {
        Detach();
        if (DataContext is not PacketInterceptorViewModel vm)
            return;
        _vm = vm;
        _packets = new UiMirror<InterceptedPacketViewModel>(vm.Packets) { Filter = Search };
        _filters = new UiMirror<PacketLogFilterViewModel>(vm.PacketFilters)
        {
            Filter = f => string.IsNullOrEmpty(FilterSearchBox.Text) || f.Content.Contains(FilterSearchBox.Text, StringComparison.OrdinalIgnoreCase),
        };
        foreach (PacketLogFilterViewModel filter in vm.PacketFilters)
            filter.PropertyChanged += Filter_PropertyChanged;
        PacketsList.ItemsSource = _packets.Items;
        FiltersList.ItemsSource = _filters.Items;
    }

    private void Detach()
    {
        if (_vm is not null)
            foreach (PacketLogFilterViewModel filter in _vm.PacketFilters)
                filter.PropertyChanged -= Filter_PropertyChanged;
        _packets?.Dispose();
        _filters?.Dispose();
        _vm = null;
    }

    // Unticking a filter hides the packets it matches.
    private void Filter_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PacketLogFilterViewModel.IsChecked))
            _packets?.Refresh();
    }

    private bool Search(InterceptedPacketViewModel packet)
    {
        if (!string.IsNullOrWhiteSpace(SearchBox.Text) && !packet.Packet.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase))
            return false;
        string[] parts = { packet.Packet };
        foreach (PacketLogFilterViewModel filter in _vm?.PacketFilters ?? new())
        {
            if (!filter.IsChecked && filter.Filter.Invoke(parts))
                return false;
        }
        return true;
    }
}
