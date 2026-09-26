using Avalonia.Controls;
using Avalonia.Interactivity;
using Skua.Core.ViewModels;

namespace Skua.App.Avalonia.Views;

public partial class CBOptionsUserControl : UserControl
{
    public CBOptionsUserControl()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        SearchBox.TextChanged += (_, _) => _options?.Refresh();
    }

    private UiMirror<DisplayOptionItemViewModelBase>? _options;

    // WPF grouped the options by DisplayType, sorted by Content, filtered by search.
    private void Attach()
    {
        _options?.Dispose();
        if (DataContext is not CBOptionsViewModel vm)
            return;
        var sorted = vm.Options.OrderBy(o => o.DisplayType.Name).ThenBy(o => o.Content).ToList();
        _options = new UiMirror<DisplayOptionItemViewModelBase>(sorted)
        {
            Filter = o => string.IsNullOrWhiteSpace(SearchBox.Text) || o.Content.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase),
        };
        OptionsList.ItemsSource = _options.Items;
    }
}
