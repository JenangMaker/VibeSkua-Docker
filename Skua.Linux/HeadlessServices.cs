using Microsoft.Extensions.DependencyInjection;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Services;

namespace Skua.Linux;

// Stand-ins for the services Skua.WPF implements with windows, dialogs and
// Win32 calls. There is no user at the other end, so anything that would ask
// takes the cautious answer and says so in the log.

public static class HeadlessServiceCollection
{
    public static IServiceCollection AddHeadlessServices(this IServiceCollection services)
    {
        services.AddSingleton<ISettingsService, HeadlessSettingsService>();
        services.AddSingleton<IDispatcherService, HeadlessDispatcher>();
        services.AddSingleton<IClipboardService, MemoryClipboard>();
        services.AddSingleton<IScreenshotService, NoScreenshots>();
        services.AddSingleton<IDialogService, HeadlessDialogs>();
        services.AddSingleton<IWindowService, NoWindows>();
        services.AddSingleton<IFileDialogService, NoFileDialogs>();
        services.AddSingleton<IHotKeyService, NoHotKeys>();
        services.AddSingleton<IThemeService, NoTheme>();
        services.AddSingleton<ISoundService, NoSound>();
        return services;
    }
}

/// <summary>The WPF app's settings service minus WPF: settings live in the same files.</summary>
public sealed class HeadlessSettingsService : ISettingsService
{
    private readonly UnifiedSettingsService _unified = new();

    public HeadlessSettingsService() => _unified.Initialize(AppRole.Client);

    public T? Get<T>(string key) => _unified.Get<T>(key);
    public T Get<T>(string key, T defaultValue) => _unified.Get(key, defaultValue);
    public void Set<T>(string key, T value) => _unified.Set(key, value);
    public void Initialize(AppRole role) => _unified.Initialize(role);
    public SharedSettings GetShared() => _unified.GetShared();
    public ClientSettings GetClient() => _unified.GetClient();
    public ManagerSettings GetManager() => _unified.GetManager();
    public void SetApplicationVersion() => _unified.SetApplicationVersion();
    public void ReloadSettings() => _unified.ReloadSettings();
}

/// <summary>No UI thread: run the action on the caller's thread, one at a time.</summary>
public sealed class HeadlessDispatcher : IDispatcherService
{
    private readonly object _lock = new();

    public void Invoke(Action action)
    {
        lock (_lock)
            action();
    }
}

public sealed class MemoryClipboard : IClipboardService
{
    private readonly Dictionary<string, object> _data = new();
    private string _text = "";

    public void SetText(string text) => _text = text;
    public string GetText() => _text;
    public void SetData(string format, object data) => _data[format] = data;
    public object GetData(string format) => _data.TryGetValue(format, out var d) ? d : null!;
}

public sealed class NoScreenshots : IScreenshotService
{
    public Task<byte[]> TakeScreenshotAsync() => Task.FromResult(Array.Empty<byte>());
}

public sealed class HeadlessDialogs : IDialogService
{
    private static void Note(string what) => Console.Error.WriteLine($"[headless] {what}");

    public bool? ShowDialog<TViewModel>(TViewModel viewModel) where TViewModel : class
    {
        Note($"dialog {typeof(TViewModel).Name} skipped");
        return null;
    }

    public bool? ShowDialog<TViewModel>(TViewModel viewModel, string title) where TViewModel : class
    {
        Note($"dialog '{title}' skipped");
        return null;
    }

    public bool? ShowDialog<TViewModel>(TViewModel viewModel, Action<TViewModel> callback) where TViewModel : class
    {
        Note($"dialog {typeof(TViewModel).Name} skipped");
        return null;
    }

    public void ShowMessageBox(string message, string caption) => Note($"{caption}: {message}");

    public bool? ShowMessageBox(string message, string caption, bool yesAndNo)
    {
        Note($"{caption}: {message} -> answered No");
        return false;
    }

    public DialogResult ShowMessageBox(string message, string caption, params string[] buttons)
    {
        Note($"{caption}: {message} [{string.Join(", ", buttons)}] -> cancelled");
        return DialogResult.Cancelled;
    }
}

public sealed class NoWindows : IWindowService
{
    public void ShowWindow<TViewModel>(int width, int height) where TViewModel : class { }
    public void ShowWindow<TViewModel>() where TViewModel : class { }
    public void ShowWindow<TViewModel>(TViewModel viewModel) where TViewModel : class { }
    public void ShowManagedWindow(string key) { }
    public void RegisterManagedWindow<TViewModel>(string key, TViewModel viewModel) where TViewModel : class, IManagedWindow { }
}

public sealed class NoFileDialogs : IFileDialogService
{
    public string? OpenFile() => null;
    public string? OpenFile(string filters) => null;
    public string? OpenFile(string initialDirectory, string filters) => null;
    public string? OpenFolder() => null;
    public string? OpenFolder(string initialDirectory) => null;
    public IEnumerable<string>? OpenText() => null;
    public string? Save() => null;
    public string? Save(string filters) => null;
    public string? Save(string initialDirectory, string filters) => null;
    public void SaveText(string contents) { }
    public void SaveText(IEnumerable<string> contents) { }
}

public sealed class NoHotKeys : IHotKeyService
{
    public void Reload() { }
    public List<T> GetHotKeys<T>() where T : IHotKey, new() => new();
    public HotKey? ParseToHotKey(string keyGesture) => null;
}

public sealed class NoTheme : IThemeService
{
#pragma warning disable CS0067 // never raised: there is no theme to change
    public event ThemeChangedEventHandler? ThemeChanged;
    public event SchemeChangedEventHandler? SchemeChanged;
#pragma warning restore CS0067
    public List<object> Presets { get; } = new();
    public List<object> UserThemes { get; } = new();
    public IEnumerable<object> ColorSelectionValues { get; } = Array.Empty<object>();
    public object ColorSelectionValue { get; set; } = new();
    public IEnumerable<object> ContrastValues { get; } = Array.Empty<object>();
    public object ContrastValue { get; set; } = new();
    public float DesiredContrastRatio { get; set; }
    public bool IsColorAdjusted { get; set; }
    public bool IsDarkTheme { get; set; } = true;
    public object? SelectedColor { get; set; }
    public ColorScheme ActiveScheme { get; set; }
    public void ApplyBaseTheme(bool isDark) => IsDarkTheme = isDark;
    public void ChangeCustomColor(object? obj) { }
    public void ChangeScheme(ColorScheme scheme) => ActiveScheme = scheme;
    public void SaveTheme(string name) { }
    public void SetCurrentTheme(object? theme) { }
    public void RemoveTheme(object? theme) { }
}

public sealed class NoSound : ISoundService
{
    public void Beep() { }
    public void Beep(int frequency, int duration) { }
}
