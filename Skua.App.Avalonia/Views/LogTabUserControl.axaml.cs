using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Skua.Core.ViewModels;

namespace Skua.App.Avalonia.Views;

public partial class LogTabUserControl : UserControl
{
    public static readonly StyledProperty<bool> ShowCopyHintProperty =
        AvaloniaProperty.Register<LogTabUserControl, bool>(nameof(ShowCopyHint), true);

    public bool ShowCopyHint
    {
        get => GetValue(ShowCopyHintProperty);
        set => SetValue(ShowCopyHintProperty, value);
    }

    public LogTabUserControl() => InitializeComponent();

    /// <summary>Copies the selected lines, or the whole log when none is selected.</summary>
    public void Copy()
    {
        if (LogList.SelectedItems is { Count: > 0 })
            Behave.CopySelected(LogList);
        else if (DataContext is LogTabViewModel vm)
            vm.CopyLogCommand.Execute(null);
    }

    public void Deselect() => LogList.UnselectAll();

    /// <summary>The log control shown inside <paramref name="host"/> (the selected tab's).</summary>
    public static LogTabUserControl? In(Visual host) => host.GetVisualDescendants().OfType<LogTabUserControl>().FirstOrDefault();

    private void Copy_Click(object? sender, RoutedEventArgs e) => Copy();
    private void Deselect_Click(object? sender, RoutedEventArgs e) => Deselect();
}
