using System.Collections.Specialized;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using Skua.Core.Interfaces;
using Skua.Core.Models;

namespace Skua.App.Avalonia.Services;

/// <summary>
/// One theme, in Skua's settings format:
/// "Name,Dark|Light,#Primary,#Secondary,#PrimaryFg,#SecondaryFg[,adjustment fields]".
/// The MaterialDesign colour-adjustment fields are kept as they are.
/// </summary>
public sealed class ThemeEntry
{
    public string Name { get; set; } = "Skua";
    public bool IsDark { get; set; } = true;
    public Color PrimaryColor { get; set; } = Color.Parse("#FF607D8B");
    public Color SecondaryColor { get; set; } = Color.Parse("#FF607D8B");
    public Color PrimaryForegroundColor { get; set; } = Colors.Black;
    public Color SecondaryForegroundColor { get; set; } = Colors.Black;
    public string Adjustment { get; set; } = "";

    public IBrush PrimaryBrush => new SolidColorBrush(PrimaryColor);
    public IBrush PrimaryForegroundBrush => new SolidColorBrush(PrimaryForegroundColor);

    public static ThemeEntry? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        string[] v = text.Split(',', StringSplitOptions.TrimEntries);
        if (v.Length < 6)
            return null;
        try
        {
            return new ThemeEntry
            {
                Name = v[0],
                IsDark = !v[1].Equals("light", StringComparison.OrdinalIgnoreCase),
                PrimaryColor = Color.Parse(v[2]),
                SecondaryColor = Color.Parse(v[3]),
                PrimaryForegroundColor = Color.Parse(v[4]),
                SecondaryForegroundColor = Color.Parse(v[5]),
                Adjustment = string.Join(",", v.Skip(6).Where(s => s.Length > 0)),
            };
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string Hex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    public string Format() =>
        $"{Name},{(IsDark ? "Dark" : "Light")},{Hex(PrimaryColor)},{Hex(SecondaryColor)},{Hex(PrimaryForegroundColor)},{Hex(SecondaryForegroundColor)}"
        + (Adjustment.Length > 0 ? "," + Adjustment : "");

    public ThemeEntry Copy(string? name = null) => Parse(Format()) is { } c ? Apply(c, name) : this;

    private static ThemeEntry Apply(ThemeEntry c, string? name)
    {
        if (name is not null)
            c.Name = name;
        return c;
    }

    public override string ToString() => Name;
}

/// <summary>
/// Skua.WPF's ThemeService on Fluent: base theme and the primary/secondary
/// colours (to the accent and Skua's brushes), presets and saved themes, the
/// current one kept in the CurrentTheme setting. MaterialDesign's automatic
/// contrast adjustment has no counterpart; its settings are carried along.
/// </summary>
public sealed partial class AvaloniaThemeService : ObservableObject, IThemeService
{
    private readonly ISettingsService _settings;
    private ThemeEntry _current;

    public AvaloniaThemeService(ISettingsService settings)
    {
        _settings = settings;
        _current = ThemeEntry.Parse(settings.Get<string>("CurrentTheme")) ?? new ThemeEntry();
    }

    public event ThemeChangedEventHandler? ThemeChanged;
    public event SchemeChangedEventHandler? SchemeChanged;

    public List<object> Presets => Themes("DefaultThemes");
    public List<object> UserThemes => Themes("UserThemes");

    private List<object> Themes(string key) =>
        (_settings.Get<StringCollection>(key)?.Cast<string>() ?? Enumerable.Empty<string>())
            .Select(ThemeEntry.Parse).OfType<ThemeEntry>().Cast<object>().ToList();

    public ThemeEntry Current => _current;

    // MaterialDesign colour adjustment: nothing to adjust here.
    public IEnumerable<object> ColorSelectionValues => Array.Empty<object>();
    public object ColorSelectionValue { get; set; } = "All";
    public IEnumerable<object> ContrastValues => Array.Empty<object>();
    public object ContrastValue { get; set; } = "Medium";
    public float DesiredContrastRatio { get; set; } = 4.5f;
    public bool IsColorAdjusted { get; set; }

    public bool IsDarkTheme
    {
        get => _current.IsDark;
        set => ApplyBaseTheme(value);
    }

    [ObservableProperty]
    private ColorScheme _activeScheme = ColorScheme.Primary;

    public object? SelectedColor
    {
        get => ActiveScheme switch
        {
            ColorScheme.Secondary => _current.SecondaryColor,
            ColorScheme.PrimaryForeground => _current.PrimaryForegroundColor,
            ColorScheme.SecondaryForeground => _current.SecondaryForegroundColor,
            _ => _current.PrimaryColor,
        };
        set => ChangeCustomColor(value);
    }

    /// <summary>Applies the saved theme; call once the application exists.</summary>
    public void ApplyCurrent() => Apply(_current, save: false);

    public void ApplyBaseTheme(bool isDark)
    {
        _current.IsDark = isDark;
        Apply(_current);
        OnPropertyChanged(nameof(IsDarkTheme));
    }

    public void ChangeCustomColor(object? obj)
    {
        if (obj is not Color color)
            return;
        switch (ActiveScheme)
        {
            case ColorScheme.Primary: _current.PrimaryColor = color; break;
            case ColorScheme.Secondary: _current.SecondaryColor = color; break;
            case ColorScheme.PrimaryForeground: _current.PrimaryForegroundColor = color; break;
            case ColorScheme.SecondaryForeground: _current.SecondaryForegroundColor = color; break;
        }
        Apply(_current);
        OnPropertyChanged(nameof(SelectedColor));
        SchemeChanged?.Invoke(ActiveScheme, color);
    }

    public void ChangeScheme(ColorScheme scheme)
    {
        ActiveScheme = scheme;
        OnPropertyChanged(nameof(SelectedColor));
    }

    public void SaveTheme(string name)
    {
        StringCollection saved = _settings.Get<StringCollection>("UserThemes") ?? new StringCollection();
        saved.Add(_current.Copy(string.IsNullOrWhiteSpace(name) ? "Custom" : name.Replace(",", " ")).Format());
        _settings.Set("UserThemes", saved);
        OnPropertyChanged(nameof(UserThemes));
    }

    public void RemoveTheme(object? theme)
    {
        if (theme is not ThemeEntry entry)
            return;
        StringCollection saved = _settings.Get<StringCollection>("UserThemes") ?? new StringCollection();
        string target = entry.Format();
        foreach (string? s in saved.Cast<string?>().ToList())
            if (s is not null && (s == target || ThemeEntry.Parse(s)?.Format() == target))
                saved.Remove(s);
        _settings.Set("UserThemes", saved);
        OnPropertyChanged(nameof(UserThemes));
    }

    public void SetCurrentTheme(object? theme)
    {
        if (theme is not ThemeEntry entry)
            return;
        _current = entry.Copy();
        Apply(_current);
        OnPropertyChanged(nameof(IsDarkTheme));
        OnPropertyChanged(nameof(SelectedColor));
        ThemeChanged?.Invoke(_current);
    }

    private void Apply(ThemeEntry theme, bool save = true)
    {
        UiThread.Invoke(() =>
        {
            if (Application.Current is not { } app)
                return;
            app.RequestedThemeVariant = theme.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
            var r = app.Resources;
            r["SystemAccentColor"] = theme.PrimaryColor;
            r["SkuaPrimaryBrush"] = new SolidColorBrush(Blend(theme.PrimaryColor, theme.IsDark ? Colors.White : Colors.Black, 0.35));
            r["SkuaSecondaryBrush"] = new SolidColorBrush(theme.SecondaryColor);
            r["SkuaPaperBrush"] = new SolidColorBrush(theme.IsDark ? Color.Parse("#303030") : Color.Parse("#FAFAFA"));
            r["SkuaBarBrush"] = new SolidColorBrush(Blend(theme.PrimaryColor, theme.IsDark ? Colors.Black : Colors.White, 0.7));
            r["SkuaDividerBrush"] = new SolidColorBrush(theme.IsDark ? Color.Parse("#1FFFFFFF") : Color.Parse("#1F000000"));
        });
        if (save)
            _settings.Set("CurrentTheme", theme.Format());
    }

    private static Color Blend(Color a, Color b, double t) => Color.FromArgb(
        255,
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));
}
