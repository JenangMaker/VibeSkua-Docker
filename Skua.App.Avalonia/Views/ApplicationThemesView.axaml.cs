using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Skua.App.Avalonia.Services;
using Skua.Core.Models;
using Skua.Core.ViewModels;

namespace Skua.App.Avalonia.Views;

public partial class ApplicationThemesView : UserControl
{
    public ApplicationThemesView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => SyncPicker();
    }

    private bool _syncing;

    private AvaloniaThemeService? Themes => (DataContext as ApplicationThemesViewModel)?.ThemeService as AvaloniaThemeService;

    private void SyncPicker()
    {
        if (Themes?.SelectedColor is not Color color)
            return;
        _syncing = true;
        Picker.Color = color;
        _syncing = false;
    }

    private void Scheme_Click(object? sender, RoutedEventArgs e)
    {
        if (Themes is { } themes && (sender as Control)?.Tag is string tag && Enum.TryParse(tag, out ColorScheme scheme))
        {
            themes.ChangeScheme(scheme);
            SyncPicker();
        }
    }

    private void Picker_ColorChanged(object? sender, ColorChangedEventArgs e)
    {
        if (!_syncing)
            Themes?.ChangeCustomColor(e.NewColor);
    }

    private void SaveTheme_Click(object? sender, RoutedEventArgs e) => Themes?.SaveTheme(ThemeName.Text ?? "");
}
