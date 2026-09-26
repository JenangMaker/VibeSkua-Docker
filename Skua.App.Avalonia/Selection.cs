using System.Windows.Input;
using Avalonia.Controls;

namespace Skua.App.Avalonia;

/// <summary>
/// WPF's ListBox.SelectedItems is an ObservableCollection&lt;object&gt;, which
/// Skua's commands take as IList&lt;object&gt;; Avalonia's is a plain IList that
/// a binding would snapshot. Views run those commands with a fresh copy.
/// </summary>
public static class Selection
{
    public static List<object> Of(ListBox list) => list.SelectedItems?.Cast<object>().ToList() ?? new();

    public static void Run(ICommand? command, ListBox list)
    {
        List<object> items = Of(list);
        if (command?.CanExecute(items) == true)
            command.Execute(items);
    }
}
