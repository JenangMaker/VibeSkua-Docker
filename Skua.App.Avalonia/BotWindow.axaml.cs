using System.Collections.ObjectModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.ViewModels;

namespace Skua.App.Avalonia;

public partial class BotWindow : Window
{
    public BotWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            Filter();
            // WPF's ListBox bound SelectedIndex too, which is what prev/next/home move.
            if (DataContext is BotWindowViewModel vm)
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(BotWindowViewModel.SelectedIndex)
                        && vm.SelectedIndex >= 0 && vm.SelectedIndex < vm.BotViews.Count)
                        vm.SelectedItem = vm.BotViews[vm.SelectedIndex];
                };
        };
        SearchBox.TextChanged += (_, _) => Filter();
        Split.PaneOpened += (_, _) => SearchBox.Focus();
    }

    // WPF filtered the drawer through a CollectionViewSource.
    private void Filter()
    {
        if (DataContext is not BotWindowViewModel vm)
            return;
        string term = SearchBox.Text ?? "";
        ViewsList.ItemsSource = string.IsNullOrEmpty(term)
            ? vm.BotViews
            : new ObservableCollection<BotControlViewModelBase>(vm.BotViews.Where(v => v.Title.Contains(term, StringComparison.OrdinalIgnoreCase)));
        ViewsList.SelectedItem = vm.SelectedItem;
    }

    private void ViewsList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Keep SelectedIndex (prev/next) in step with a pick from a filtered list.
        if (DataContext is BotWindowViewModel vm && ViewsList.SelectedItem is BotControlViewModelBase item)
        {
            int index = vm.BotViews.IndexOf(item);
            if (index >= 0 && vm.SelectedIndex != index)
                vm.SelectedIndex = index;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (DataContext is { } vm)
        {
            StrongReferenceMessenger.Default.UnregisterAll(vm);
            (vm as IDisposable)?.Dispose();
        }
        DataContext = null;
    }
}
