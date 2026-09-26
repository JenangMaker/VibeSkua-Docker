using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Skua.Core.ViewModels;

namespace Skua.App.Avalonia.Views;

public partial class ScriptRepoView : UserControl
{
    public ScriptRepoView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        SearchBox.TextChanged += (_, _) =>
        {
            // Debounced, as the WPF view did (250 ms).
            _debounce?.Dispose();
            _debounce = DispatcherTimer.RunOnce(() => _scripts?.Refresh(), TimeSpan.FromMilliseconds(250));
        };
        DetachedFromVisualTree += (_, _) => _scripts?.Dispose();
    }

    private UiMirror<ScriptInfoViewModel>? _scripts;
    private IDisposable? _debounce;

    // The view model's list changes on background threads (WPF used
    // EnableCollectionSynchronization); show a UI-thread copy, filtered by
    // the search box.
    private void Attach()
    {
        _scripts?.Dispose();
        _scripts = null;
        if (DataContext is not ScriptRepoViewModel vm)
            return;
        _scripts = new UiMirror<ScriptInfoViewModel>(vm.Scripts) { Filter = Search };
        ScriptsGrid.ItemsSource = _scripts.Items;
    }

    private bool Search(ScriptInfoViewModel script)
    {
        string term = SearchBox.Text ?? "";
        if (string.IsNullOrWhiteSpace(term))
            return true;
        return script.Info.Name?.Contains(term, StringComparison.OrdinalIgnoreCase) == true
            || script.Info.FileName?.Contains(term, StringComparison.OrdinalIgnoreCase) == true
            || script.Info.FilePath?.Contains(term, StringComparison.OrdinalIgnoreCase) == true
            || script.Info.Description?.Contains(term, StringComparison.OrdinalIgnoreCase) == true
            || script.InfoTags.Any(tag => tag.Contains(term, StringComparison.OrdinalIgnoreCase));
    }
}
