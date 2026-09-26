using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Skua.Core.ViewModels;
using Skua.Core.Models.Quests;

namespace Skua.App.Avalonia.Views;

public partial class LoaderView : UserControl
{
    public LoaderView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        SearchBox.TextChanged += (_, _) => _quests?.Refresh();
        QuestIDsListBox.SelectionChanged += (_, _) => FakeComplete.IsEnabled = QuestIDsListBox.SelectedItems?.Count == 1;
        QuestIDsListBox.KeyDown += (_, e) =>
        {
            if (DataContext is not LoaderViewModel vm || !e.KeyModifiers.HasFlag(KeyModifiers.Control))
                return;
            if (e.Key == Key.L)
                Selection.Run(vm.LoadQuestsCommand, QuestIDsListBox);
            else if (e.Key == Key.C)
                Selection.Run(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? vm.CopyQuestsNamesCommand : vm.CopyQuestsIDsCommand, QuestIDsListBox);
            else
                return;
            e.Handled = true;
        };
        AttachedToVisualTree += async (_, _) =>
        {
            // WPF loaded the quest list when the view first appeared.
            if (_loaded || DataContext is not LoaderViewModel vm)
                return;
            _loaded = true;
            await vm.GetQuestsCommand.ExecuteAsync(false);
        };
    }

    private UiMirror<QuestData>? _quests;
    private bool _loaded;

    private LoaderViewModel? Vm => DataContext as LoaderViewModel;

    private void Attach()
    {
        _quests?.Dispose();
        if (Vm is not { } vm)
            return;
        _quests = new UiMirror<QuestData>(vm.QuestIDs)
        {
            Filter = q => string.IsNullOrEmpty(SearchBox.Text) || q.ToString().Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase),
        };
        QuestIDsListBox.ItemsSource = _quests.Items;
    }

    private void CopyIds_Click(object? sender, RoutedEventArgs e) => Selection.Run(Vm?.CopyQuestsIDsCommand, QuestIDsListBox);
    private void CopyNames_Click(object? sender, RoutedEventArgs e) => Selection.Run(Vm?.CopyQuestsNamesCommand, QuestIDsListBox);
    private void CopyNamesAndIds_Click(object? sender, RoutedEventArgs e) => Selection.Run(Vm?.CopyQuestsNamesAndIDsCommand, QuestIDsListBox);
    private void LoadQuests_Click(object? sender, RoutedEventArgs e) => Selection.Run(Vm?.LoadQuestsCommand, QuestIDsListBox);

    private void FakeComplete_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm?.UpdateSelectedQuestCommand is { } command && command.CanExecute(QuestIDsListBox.SelectedItem))
            command.Execute(QuestIDsListBox.SelectedItem);
    }
}
