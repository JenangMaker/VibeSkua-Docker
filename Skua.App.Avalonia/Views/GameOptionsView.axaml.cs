using Avalonia.Controls;
using Avalonia.Interactivity;
using Skua.Core.ViewModels;

namespace Skua.App.Avalonia.Views;

public partial class GameOptionsView : UserControl
{
    public GameOptionsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        SearchBox.TextChanged += (_, _) => _options?.Refresh();
    }

    private UiMirror<DisplayOptionItemViewModelBase>? _options;

    // WPF sorted the options by Tag then Content and filtered by the search box.
    private void Attach()
    {
        _options?.Dispose();
        if (DataContext is not GameOptionsViewModel vm)
            return;
        var sorted = vm.GameOptions.OrderBy(o => o.Tag).ThenBy(o => o.Content).ToList();
        _options = new UiMirror<DisplayOptionItemViewModelBase>(sorted)
        {
            Filter = o => string.IsNullOrEmpty(SearchBox.Text) || o.Content.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase),
        };
        OptionsList.ItemsSource = _options.Items;
    }
}
