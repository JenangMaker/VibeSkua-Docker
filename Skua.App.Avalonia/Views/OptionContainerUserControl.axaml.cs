using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Skua.Core.Interfaces;
using Skua.Core.ViewModels;
using Skua.Linux;

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
            ShowSkipAtStart();
        };
    }

    private bool _loadingSkip;

    // A script's options (not the plugin or other containers) can be kept
    // from opening at the script's start.
    private void ShowSkipAtStart()
    {
        var script = (DataContext as OptionContainerViewModel)?.Container as IScriptOptionContainer;
        SkipAtStart.IsVisible = script is not null;
        if (script is null)
            return;
        _loadingSkip = true;
        SkipAtStart.IsChecked = ScriptOptionsWindow.IsSkipped(script.Storage);
        SkipAtStart.IsEnabled = !ScriptOptionsWindow.SkipAll;
        _loadingSkip = false;
    }

    private void SkipAtStart_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loadingSkip || (DataContext as OptionContainerViewModel)?.Container is not IScriptOptionContainer script)
            return;
        ScriptOptionsWindow.Set(script.Storage, SkipAtStart.IsChecked == true);
    }

    public sealed record OptionGroup(string Name, bool IsExpanded, List<OptionContainerItemViewModel> Items);

    // Clicking anywhere on a row selects it, for the description below.
    private void Row_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: OptionContainerItemViewModel item } && DataContext is OptionContainerViewModel vm)
            vm.SelectedOption = item;
    }
}
