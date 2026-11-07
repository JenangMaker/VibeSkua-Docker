using Avalonia.Controls;
using Skua.Core.ViewModels;

namespace Skua.App.Avalonia.Views;

public partial class PluginsView : UserControl
{
    public PluginsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        SearchBox.TextChanged += (_, _) => _plugins?.Refresh();
    }

    private UiMirror<PluginItemViewModel>? _plugins;

    // Search by name, author or description.
    private void Attach()
    {
        _plugins?.Dispose();
        if (DataContext is not PluginsViewModel vm)
            return;
        _plugins = new UiMirror<PluginItemViewModel>(vm.Plugins) { Filter = Matches };
        PluginList.ItemsSource = _plugins.Items;
    }

    private bool Matches(PluginItemViewModel item)
    {
        string search = SearchBox.Text?.Trim() ?? "";
        if (search.Length == 0)
            return true;
        var plugin = item.Container.Plugin;
        return plugin.Name?.Contains(search, StringComparison.OrdinalIgnoreCase) == true
            || plugin.Author?.Contains(search, StringComparison.OrdinalIgnoreCase) == true
            || plugin.Description?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;
    }
}
