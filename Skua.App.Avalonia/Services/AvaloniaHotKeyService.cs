using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.Models;

namespace Skua.App.Avalonia.Services;

/// <summary>
/// Skua.WPF's HotKeyService: the "Binding|Gesture" pairs in the HotKeys
/// setting become key bindings on the main window. Like WPF's, they work
/// while a Skua window has focus (not globally).
/// </summary>
public sealed class AvaloniaHotKeyService(Dictionary<string, IRelayCommand> hotKeys, ISettingsService settings) : IHotKeyService
{
    private static readonly string[] DefaultHotKeys =
    {
        "ToggleScript", "LoadScript", "OpenBank", "OpenConsole", "ToggleAutoAttack", "ToggleAutoHunt", "ToggleLagKiller",
    };

    private readonly List<KeyBinding> _registered = new();

    private static Window? MainWindow =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public void Reload() => UiThread.Invoke(() =>
    {
        Clear();
        if (MainWindow is not { } window)
            return;
        foreach (string? hk in Load())
        {
            if (string.IsNullOrEmpty(hk))
                continue;
            string[] split = hk.Split('|');
            if (split.Length < 2 || !hotKeys.TryGetValue(split[0], out var command))
                continue;
            string gesture = split[1].Trim();
            if (gesture.Length == 0 || gesture.Equals("Unassigned", StringComparison.OrdinalIgnoreCase)
                || gesture.StartsWith("Failed to bind", StringComparison.OrdinalIgnoreCase))
                continue;
            if (Parse(gesture) is not { } keyGesture)
            {
                StrongReferenceMessenger.Default.Send<HotKeyErrorMessage>(new(split[0]));
                continue;
            }
            KeyBinding binding = new() { Gesture = keyGesture, Command = command };
            window.KeyBindings.Add(binding);
            _registered.Add(binding);
        }
    });

    private void Clear()
    {
        if (MainWindow is { } window)
            foreach (KeyBinding binding in _registered)
                window.KeyBindings.Remove(binding);
        _registered.Clear();
    }

    public List<T> GetHotKeys<T>() where T : IHotKey, new()
    {
        List<T> parsed = new();
        foreach (string? hk in Load())
        {
            if (string.IsNullOrEmpty(hk))
                continue;
            string[] split = hk.Split('|');
            parsed.Add(new()
            {
                Binding = split[0],
                Title = Skua.Core.AppStartup.HotKeys.GetFormattedTitle(split[0]),
                Description = Skua.Core.AppStartup.HotKeys.GetDescription(split[0]),
                KeyGesture = split.Length > 1 ? split[1] : string.Empty,
            });
        }
        return parsed;
    }

    public HotKey? ParseToHotKey(string keyGesture) =>
        Parse(keyGesture) is { } g
            ? new HotKey(g.Key.ToString(), g.KeyModifiers.HasFlag(KeyModifiers.Control), g.KeyModifiers.HasFlag(KeyModifiers.Alt), g.KeyModifiers.HasFlag(KeyModifiers.Shift))
            : null;

    private StringCollection Load()
    {
        StringCollection? hotkeys = settings.Get<StringCollection>("HotKeys");
        if (hotkeys is null)
        {
            hotkeys = new StringCollection();
            EnsureAllBindingsExist(hotkeys);
            settings.Set("HotKeys", hotkeys);
        }
        return hotkeys;
    }

    /// <summary>"Ctrl+Shift+F5"-style gestures, as WPF's parser read them.</summary>
    private static KeyGesture? Parse(string keyGesture)
    {
        string lower = keyGesture.ToLowerInvariant();
        KeyModifiers modifiers = KeyModifiers.None;
        if (lower.Contains("alt")) modifiers |= KeyModifiers.Alt;
        if (lower.Contains("shift")) modifiers |= KeyModifiers.Shift;
        if (lower.Contains("ctrl") || lower.Contains("ctl")) modifiers |= KeyModifiers.Control;
        string key = lower.Replace("+", "").Replace("alt", "").Replace("shift", "").Replace("ctrl", "").Replace("ctl", "").Trim();
        return key.Length > 0 && Enum.TryParse(key, ignoreCase: true, out Key parsed) && parsed != Key.None
            ? new KeyGesture(parsed, modifiers)
            : null;
    }

    private static void EnsureAllBindingsExist(StringCollection hotkeys)
    {
        HashSet<string> existing = new();
        HashSet<string> usedGestures = new(StringComparer.OrdinalIgnoreCase);
        foreach (string? hk in hotkeys)
        {
            if (string.IsNullOrWhiteSpace(hk))
                continue;
            string[] split = hk.Split('|');
            if (split.Length > 0 && !string.IsNullOrWhiteSpace(split[0]))
                existing.Add(split[0]);
            if (split.Length > 1 && !string.IsNullOrWhiteSpace(split[1]))
                usedGestures.Add(split[1]);
        }
        foreach (string key in DefaultHotKeys)
        {
            if (existing.Contains(key))
                continue;
            string gesture = key == "ToggleLagKiller" && !usedGestures.Contains("F6") ? "F6" : string.Empty;
            hotkeys.Add($"{key}|{gesture}");
        }
    }
}
