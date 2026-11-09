using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Skua.App.Avalonia.Services;

/// <summary>
/// Fills the 2.x accent keys (Theming/Colors.axaml) from one theme's colours. Follows
/// upstream 2.1.2's ThemeResourceApplicator.ApplyAccentBrushes, without its Material and
/// native title bar parts (we have neither).
/// </summary>
internal static class ThemeBrushes
{
    // Glyphs drawn on an accent fill use the theme's text-on-accent colour, not Fluent's white.
    private static readonly string[] FluentOnAccentBrushKeys =
    [
        "CheckBoxCheckGlyphForegroundChecked", "CheckBoxCheckGlyphForegroundCheckedPointerOver", "CheckBoxCheckGlyphForegroundCheckedPressed",
        "CheckBoxCheckGlyphForegroundIndeterminate", "CheckBoxCheckGlyphForegroundIndeterminatePointerOver", "CheckBoxCheckGlyphForegroundIndeterminatePressed",
        "RadioButtonCheckGlyphFill", "RadioButtonCheckGlyphFillPointerOver", "RadioButtonCheckGlyphFillPressed",
        "ToggleButtonForegroundChecked", "ToggleButtonForegroundCheckedPointerOver", "ToggleButtonForegroundCheckedPressed",
        "ToggleSwitchKnobFillOn", "ToggleSwitchKnobFillOnPointerOver", "ToggleSwitchKnobFillOnPressed"
    ];

    public static void Apply(Application app, Color primary, Color primaryForeground, bool isDark)
    {
        IResourceDictionary r = app.Resources;
        SetColor(r, "SkuaAccentColor", primary);
        SetColor(r, "SkuaAccentForegroundColor", primaryForeground);
        SetBrush(r, "SkuaAccentBrush", primary);
        SetBrush(r, "SkuaAccentForegroundBrush", primaryForeground);
        SetBrush(r, "SkuaAccentHoverBrush", Lighten(primary, 0.1));
        SetBrush(r, "SkuaAccentPressedBrush", Darken(primary, 0.12));

        // Fluent controls (check box, radio, switch, slider, progress bar, checked toggle
        // button, tab pipe, text box focus) read these.
        foreach (string key in FluentOnAccentBrushKeys)
            SetBrush(r, key, primaryForeground);
        SetColor(r, "SystemAccentColor", primary);
        SetColor(r, "SystemAccentColorLight1", Lighten(primary, 0.1));
        SetColor(r, "SystemAccentColorLight2", Lighten(primary, 0.2));
        SetColor(r, "SystemAccentColorLight3", Lighten(primary, 0.3));
        SetColor(r, "SystemAccentColorDark1", Darken(primary, 0.15));
        SetColor(r, "SystemAccentColorDark2", Darken(primary, 0.3));
        SetColor(r, "SystemAccentColorDark3", Darken(primary, 0.45));

        // Selected/on fill for tabs, toggles, rows and chips; the strong one is its hover.
        SetBrush(r, "SkuaAccentTintBrush", WithAlpha(primary, 0x24));
        SetBrush(r, "SkuaAccentTintStrongBrush", WithAlpha(primary, 0x38));

        // Selection and tag colours differ per variant. Colors.axaml is a merged dictionary;
        // theme dictionaries on the app's own resources are looked up before it.
        {
            IResourceDictionary l = VariantDictionary(r, ThemeVariant.Light);
            SetBrush(l, "SkuaSelectionBrush", WithAlpha(primary, 0x33));
            SetBrush(l, "SkuaTagBackgroundBrush", WithAlpha(primary, 0x1F));
            SetBrush(l, "SkuaTagBackgroundHoverBrush", WithAlpha(primary, 0x35));
            SetBrush(l, "SkuaTagBorderBrush", WithAlpha(primary, 0x45));
            SetBrush(l, "SkuaTagBorderHoverBrush", WithAlpha(primary, 0x75));
            SetBrush(l, "SkuaTagTextBrush", Darken(primary, 0.20));
        }
        {
            IResourceDictionary d = VariantDictionary(r, ThemeVariant.Dark);
            SetBrush(d, "SkuaSelectionBrush", WithAlpha(primary, 0x66));
            SetBrush(d, "SkuaTagBackgroundBrush", WithAlpha(primary, 0x22));
            SetBrush(d, "SkuaTagBackgroundHoverBrush", WithAlpha(primary, 0x3B));
            SetBrush(d, "SkuaTagBorderBrush", WithAlpha(primary, 0x4D));
            SetBrush(d, "SkuaTagBorderHoverBrush", WithAlpha(primary, 0x80));
            SetBrush(d, "SkuaTagTextBrush", Lighten(primary, 0.18));
        }

        // 1.x key: accent-coloured text (menus, flat buttons) until phase 2 restyles them.
        SetBrush(r, "SkuaPrimaryBrush", isDark ? Lighten(primary, 0.35) : Darken(primary, 0.35));
    }

    private static IResourceDictionary VariantDictionary(IResourceDictionary r, ThemeVariant variant)
    {
        if (r.ThemeDictionaries.TryGetValue(variant, out IThemeVariantProvider? p) && p is IResourceDictionary existing)
            return existing;
        var created = new ResourceDictionary();
        r.ThemeDictionaries[variant] = created;
        return created;
    }

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static void SetBrush(IResourceDictionary dict, string key, Color color)
    {
        if (dict.TryGetValue(key, out object? existing) && existing is SolidColorBrush brush)
        {
            if (brush.Color != color)
                brush.Color = color;
        }
        else
        {
            dict[key] = new SolidColorBrush(color);
        }
    }

    private static void SetColor(IResourceDictionary dict, string key, Color color)
    {
        if (dict.TryGetValue(key, out object? existing) && existing is Color c && c == color)
            return;
        dict[key] = color;
    }

    private static Color Lighten(Color color, double amount)
    {
        byte L(byte c) => (byte)Math.Clamp(c + (255 - c) * amount, 0, 255);
        return Color.FromArgb(color.A, L(color.R), L(color.G), L(color.B));
    }

    private static Color Darken(Color color, double amount)
    {
        byte D(byte c) => (byte)Math.Clamp(c * (1 - amount), 0, 255);
        return Color.FromArgb(color.A, D(color.R), D(color.G), D(color.B));
    }
}
